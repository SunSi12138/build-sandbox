using System.Globalization;
using System.Text.Json.Serialization;

namespace StreetEcoTool;

public sealed record TypeReminder(string PointId, string PointName, string CommunityId, string CommunityName, string TypeId, string TypeName, string PeriodFrom, string PeriodTo, string? LastDate);
public sealed record TypeReminderGroup(int Count, TypeReminder[] Items);
public sealed record StatisticsRow(string Id, string Name, int Places, int CheckedPlaces, int Inspections, int Incomplete, int Issues, int Closed, int Overdue);
public sealed record StatisticsDto(string From, string To, StatisticsRow Total, StatisticsRow[] Grids, StatisticsRow[] Types);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TypeReminder[]))]
[JsonSerializable(typeof(StatisticsDto))]
public partial class StatisticsJsonContext : JsonSerializerContext;

public sealed partial class Store
{
    public TypeReminder[] GetTypeReminders()
    {
        lock (_gate)
        {
            var today = DateOnly.ParseExact(Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var result = new List<TypeReminder>();
            foreach (var type in ListCommunityRecords("inspection-type"))
            {
                var frequency = JsonText(type.Data, "frequency");
                if (frequency is not ("weekly" or "monthly" or "quarterly")) continue;
                var from = ReminderRules.PeriodStart(frequency, today)!.Value;
                var to = frequency == "weekly" ? from.AddDays(6) : frequency == "monthly" ? from.AddMonths(1).AddDays(-1) : from.AddMonths(3).AddDays(-1);
                using var query = Command("""
                    SELECT p.id,p.name,p.community_id,c.name,
                      (SELECT MAX(n.inspection_date) FROM active_inspections n WHERE n.point_id=p.id AND n.inspection_type_id=$type AND n.result<>'incomplete' AND n.inspection_date<=$today)
                    FROM points p JOIN communities c ON c.id=p.community_id
                    WHERE p.disabled=0 AND p.temporary=0 AND ($grid='' OR p.community_id=$grid) AND ($pointType='' OR p.type_id=$pointType)
                      AND NOT EXISTS(SELECT 1 FROM active_inspections n WHERE n.point_id=p.id AND n.inspection_type_id=$type AND n.result<>'incomplete' AND n.inspection_date>=$from AND n.inspection_date<=$today)
                    ORDER BY c.name,p.name;
                    """, null, ("$type", type.Id), ("$grid", JsonText(type.Data, "communityId")), ("$pointType", JsonText(type.Data, "pointTypeId")), ("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$today", Today));
                using var rows = query.ExecuteReader();
                while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), type.Id, type.Name, from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), NullableString(rows, 4)));
            }
            return [.. result];
        }
    }

    public StatisticsDto GetStatistics(string? from, string? to, string? communityId)
    {
        lock (_gate)
        {
            var start = DateText(string.IsNullOrWhiteSpace(from) ? Today[..8] + "01" : from, "开始日期");
            var end = DateText(string.IsNullOrWhiteSpace(to) ? Today : to, "结束日期"); CheckDateRange(start, end);
            var records = ReadInspectionList("WHERE n.inspection_date>=$from AND n.inspection_date<=$to AND ($grid='' OR n.community_id=$grid);", [("$from", start), ("$to", end), ("$grid", communityId ?? "")]);
            var places = new List<PointDto>();
            for (var page = 1;; page++) { var batch = ListPoints(new(CommunityId: communityId, Page: page, PageSize: 100)); places.AddRange(batch.Items); if (page * batch.PageSize >= batch.Total) break; }
            StatisticsRow Row(string id, string name, InspectionDto[] list, int placeCount) => new(id, name, placeCount, list.Where(x => x.Result != "incomplete").Select(x => x.PointId).Distinct().Count(), list.Length, list.Count(x => x.Result == "incomplete"), list.Sum(x => x.IssueCount), list.Sum(x => x.ClosedCount), list.Sum(x => x.OverdueCount));
            var grids = GetCommunities().Where(x => string.IsNullOrEmpty(communityId) || x.Id == communityId).Select(x => Row(x.Id, x.Name, records.Where(n => n.CommunityId == x.Id).ToArray(), places.Count(p => p.CommunityId == x.Id))).ToArray();
            var types = records.GroupBy(x => x.InspectionTypeId).Select(x => Row(x.Key, x.First().InspectionTypeName.Length > 0 ? x.First().InspectionTypeName : "未分类", x.ToArray(), 0)).ToArray();
            return new(start, end, Row("all", "合计", records, places.Count), grids, types);
        }
    }
    public void MapStatisticsApi(WebApplication app)
    {
        RequestDelegate reminders = c => Api.Write(c, GetTypeReminders(), StatisticsJsonContext.Default.TypeReminderArray);
        RequestDelegate statistics = c => Api.Write(c, GetStatistics(c.Request.Query["from"], c.Request.Query["to"], c.Request.Query["communityId"]), StatisticsJsonContext.Default.StatisticsDto);
        app.MapGet("/api/type-reminders", reminders); app.MapGet("/api/statistics", statistics);
    }
}
