using System.Globalization;

namespace StreetEcoTool;

public sealed partial class Store
{
    public DashboardDto GetDashboard(DateOnly? asOf = null)
    {
        lock (_gate)
        {
            var date = asOf ?? DateOnly.FromDateTime(DateTime.Today);
            var today = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var openIssues = ReadIssueList("WHERE x.status<>'closed' ORDER BY x.deadline,x.rowid;", today, []);
            var overdue = openIssues.Where(issue => ReminderRules.IsOverdue(DateOnly.ParseExact(issue.Deadline, "yyyy-MM-dd", CultureInfo.InvariantCulture), issue.Status, date)).ToArray();
            var soon = openIssues.Where(issue => ReminderRules.IsDueSoon(DateOnly.ParseExact(issue.Deadline, "yyyy-MM-dd", CultureInfo.InvariantCulture), issue.Status, date)).ToArray();
            var awaiting = openIssues.Where(issue => ReminderRules.IsAwaitingReview(issue.Status)).ToArray();
            var points = new List<PointDto>();
            using (var command = Command(PointSelect + " WHERE p.disabled=0 AND p.temporary=0 AND p.frequency<>'irregular' ORDER BY p.name,p.rowid;", null, ("$today", today)))
            using (var rows = command.ExecuteReader())
                while (rows.Read()) points.Add(ReadPoint(rows));
            var missed = points.Where(point => ReminderRules.NeedsInspection(point.Frequency, point.Disabled,
                point.LastInspectionDate is null ? null : DateOnly.ParseExact(point.LastInspectionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), date, point.Temporary)).ToArray();
            var active = ReadInspectionList("WHERE EXISTS(SELECT 1 FROM issues x WHERE x.inspection_id=n.id AND x.status<>'closed') ORDER BY n.inspection_date DESC,n.rowid DESC;", [], today);
            var todo = active.OrderBy(item => item.OverdueCount > 0 ? 0 : soon.Any(issue => issue.InspectionId == item.Id) ? 1 : item.RectifyStatus == "feedback" ? 2 : 3)
                .ThenBy(item => item.NearestDeadline, StringComparer.Ordinal).Take(5).ToArray();
            var recent = ReadInspectionList("ORDER BY n.inspection_date DESC,n.rowid DESC LIMIT 5;", [], today);
            return new(today, new(overdue.Length, overdue.Take(8).ToArray()), new(soon.Length, soon.Take(8).ToArray()),
                new(awaiting.Length, awaiting.Take(8).ToArray()), new(missed.Length, missed.Take(5).ToArray()), todo, recent, GetMatterReminders());
        }
    }

    public bool SeedDemo()
    {
        lock (_gate)
        {
            using (var existing = Command("SELECT (SELECT COUNT(*) FROM points)+(SELECT COUNT(*) FROM inspections)+(SELECT COUNT(*) FROM communities);"))
                if (Convert.ToInt32(existing.ExecuteScalar()) > 0) return false;
            var settings = GetSettings();
            SaveSettings(settings with
            {
                StreetName = settings.StreetName.Length == 0 ? "示例街道办事处" : settings.StreetName,
                IssuingUnit = settings.IssuingUnit.Length == 0 ? "示例街道办事处" : settings.IssuingUnit,
                DefaultInspector = settings.DefaultInspector.Length == 0 ? "环保工作人员" : settings.DefaultInspector
            });
            var east = SaveCommunity(null, new("东苑社区", "东苑社区居民委员会"));
            var west = SaveCommunity(null, new("西河社区", "西河社区居民委员会"));
            var types = GetPointTypes();
            string Type(string name) => types.First(type => type.Name == name).Id;
            var restaurant = SavePoint(null, new("惠民早餐店", east.Id, Type("餐饮油烟"), "建设路 12 号", "张店长", "13800000000", "weekly", "示例数据，仅供试用。"));
            var construction = SavePoint(null, new("建设路改造工地", east.Id, Type("工地扬尘"), "建设路与和平路交叉口", "李负责人", "13800000001", "weekly"));
            var river = SavePoint(null, new("中心河西河桥段", west.Id, Type("河道水体"), "西河桥至滨河步道段", "王联系人", "13800000002", "monthly"));
            var rubbish = SavePoint(null, new("西河便民垃圾收集点", west.Id, Type("垃圾堆放"), "西河路 36 号旁", "刘联系人", "13800000003", "weekly"));
            _ = SavePoint(null, new("旧厂房仓储点", east.Id, Type("小散乱污"), "工业路 8 号", "", "", "quarterly"));
            _ = SavePoint(null, new("已停用示例点位", west.Id, Type("其他"), "示例巷 5 号", "", "", "weekly", "已停用点位不纳入周期提醒。", true));
            string Day(int offset) => DateTime.Today.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var inspector = GetSettings().DefaultInspector;
            var issueType = GetIssueTypes()[0].Id;
            CreateInspection(new(restaurant.Id, Today, inspector, "normal", "油烟净化设施已正常运行，清洗台账齐全。"));
            var problem = CreateInspection(new(construction.Id, Day(-7), inspector, "issues", "现场排查发现两项问题。", "示例-2026-001",
                [new("裸露土方覆盖不到位，周边有明显扬尘。", "及时覆盖裸土，落实洒水降尘措施。", Day(-2), IssueTypeId: issueType),
                 new("出入口车辆冲洗设施需维护。", "检修冲洗设施并保持路面整洁。", Day(2), IssueTypeId: issueType)]));
            RecordFeedback(problem.Issues[1].Id, new(Day(-1), "社区反馈已安排维修，待现场复查。"));
            var water = CreateInspection(new(river.Id, Day(-12), inspector, "issues", "河岸局部发现杂物。", "",
                [new("河岸有零散生活垃圾。", "清理河岸垃圾并加强日常巡查。", Day(-5), IssueTypeId: issueType)]));
            RecordFeedback(water.Issues[0].Id, new(Day(-8), "已完成河岸清理。"));
            ReviewAndClose(water.Issues[0].Id, new(Day(-7), "现场复查清理到位，予以闭环。"));
            CreateInspection(new(rubbish.Id, Day(-6), inspector, "issues", "", "",
                [new("垃圾收集点周边散落杂物，部分垃圾桶盖未关闭。", "清扫周边环境并规范垃圾桶使用。", Today, IssueTypeId: issueType)]));
            CreateInspection(new(null, Today, inspector, "issues", Issues: [new("巷口有散落垃圾。", "清理散落垃圾。", Day(5), IssueTypeId: issueType)],
                NewPoint: new("东苑北巷临时排查点", east.Id, Type("垃圾堆放"), "东苑北巷路口", Longitude: 88.893426, Latitude: 29.274331)));
            CreateInspection(new(restaurant.Id, Day(-14), inspector, "normal"));
            SaveCampaign(null, new("西河沿岸卫生大扫除", GetCampaignTypes()[0].Id, river.Id, Day(-2),
                "清扫河岸步道，清运积存垃圾。", Organizer: GetSettings().IssuingUnit, Participants: "社区工作人员及志愿者", ParticipantCount: 24,
                VehicleCount: 2, GarbageTons: 1.25m, AreaSqm: 1200m, Outcome: "河岸步道清理完成。"));
            return true;
        }
    }
}
