using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace StreetEcoTool;
public sealed record ReportDto(string Title, string Scope, string[] Headers, string[][] Rows);
public sealed record ReportQuery(string Kind, string From = "", string To = "", string CommunityId = "", string Status = "", string Search = "", string TypeId = "", string PlanId = "");
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReportDto))]
public partial class ReportJsonContext : JsonSerializerContext;

public sealed partial class Store
{
    public ReportDto GetReport(ReportQuery query)
    {
        lock (_gate)
        {
            var from = string.IsNullOrEmpty(query.From) ? "0001-01-01" : DateText(query.From, "开始日期");
            var to = string.IsNullOrEmpty(query.To) ? "9999-12-31" : DateText(query.To, "结束日期");
            CheckDateRange(from, to);
            var rows = new List<string[]>();
            string title; string[] headers;
            bool Match(string value) => string.IsNullOrEmpty(query.Search) || value.Contains(query.Search, StringComparison.OrdinalIgnoreCase);
            var scope = $"网格：{GetCommunities().FirstOrDefault(x => x.Id == query.CommunityId)?.Name ?? "全部"}；状态：{ReportStatus(query.Status)}";
            if (query.Kind == "places")
            {
                title = "场所台账"; headers = ["编号", "名称", "网格", "场所形态", "地址", "联系人", "电话", "状态"];
                for (var page = 1; ; page++)
                {
                    var data = ListPoints(new(Q: query.Search, CommunityId: query.CommunityId, TypeId: query.TypeId, Disabled: query.Status is "true" or "false" ? query.Status : "all", Page: page, PageSize: 200, Temporary: "false"));
                    rows.AddRange(data.Items.Select(x => new[] { x.Id, x.Name, x.CommunityName, x.TypeName, x.Address, x.Contact, x.Phone, x.Disabled ? "已停用" : "使用中" }));
                    if (page * data.PageSize >= data.Total) break;
                }
                scope = "当前场所资料；" + scope;
            }
            else if (query.Kind == "uses")
            {
                title = "经营使用台账"; headers = ["编号", "场所", "名称", "用途", "联系人", "电话", "开始日期", "结束日期", "状态"];
                foreach (var record in ListCommunityRecords("use", includeArchived: true))
                {
                    var point = GetPointInternal(record.ParentId);
                    if (query.CommunityId.Length > 0 && point.CommunityId != query.CommunityId || !Match(record.Name + point.Name)) continue;
                    var state = JsonText(record.Data, "state");
                    if (query.Status.Length > 0 && query.Status != state) continue;
                    var purposes = record.Data.TryGetProperty("purposes", out var p) ? string.Join("、", p.EnumerateArray().Select(x => x.GetString())) : "";
                    rows.Add([record.Id, point.Name, record.Name, purposes, JsonText(record.Data,"contact"),JsonText(record.Data,"phone"),JsonText(record.Data,"startDate"),JsonText(record.Data,"endDate"),state]);
                }
            }
            else if (query.Kind == "inspections")
            {
                title = "检查台账"; headers = ["编号", "检查日期", "场所", "经营使用", "网格", "检查类型", "检查人", "结果", "问题数", "完成数"];
                var records = ReadInspectionList("WHERE n.inspection_date>=$from AND n.inspection_date<=$to AND ($grid='' OR n.community_id=$grid) ORDER BY n.inspection_date DESC,n.rowid DESC;", [("$from", from), ("$to", to), ("$grid", query.CommunityId)]);
                foreach (var x in records)
                {
                    if (!Match(x.PointName + x.UseName + x.Inspector) || query.Status.Length > 0 && query.Status != x.Result && query.Status != x.RectifyStatus || query.TypeId.Length > 0 && query.TypeId != x.InspectionTypeId) continue;
                    rows.Add([x.Id, x.Date, x.PointName, x.UseName, x.CommunityName, x.InspectionTypeName, x.Inspector, ReportStatus(x.Result), x.IssueCount.ToString(CultureInfo.InvariantCulture), x.ClosedCount.ToString(CultureInfo.InvariantCulture)]);
                }
                scope = $"检查日期：{(query.From.Length == 0 ? "不限" : query.From)} 至 {(query.To.Length == 0 ? "不限" : query.To)}；" + scope;
            }
            else if (query.Kind == "issues")
            {
                title = "问题整改台账"; headers = ["编号", "发现日期", "场所", "问题", "整改要求", "责任单位", "整改期限", "状态", "完成日期"];
                var records = ReadIssueList("WHERE n.inspection_date>=$from AND n.inspection_date<=$to AND ($grid='' OR n.community_id=$grid) ORDER BY x.deadline,x.rowid;", Today, [("$from", from), ("$to", to), ("$grid", query.CommunityId)]);
                foreach (var x in records)
                {
                    if (!Match(x.PointName + x.Description + x.ResponsibleUnit)) continue;
                    if (query.Status == "overdue" ? !x.Overdue : query.Status.Length > 0 && query.Status != x.Status) continue;
                    rows.Add([x.Id,x.InspectionDate,x.PointName,x.Description,x.Requirement,x.ResponsibleUnit,x.Deadline,ReportStatus(x.Status) + (x.Overdue ? "（超期）" : ""),x.ClosedDate ?? ""]);
                }
                scope = $"发现日期：{(query.From.Length == 0 ? "不限" : query.From)} 至 {(query.To.Length == 0 ? "不限" : query.To)}；" + scope;
            }
            else if (query.Kind == "plans")
            {
                title = "检查安排进度"; headers = ["安排编号", "安排名称", "检查人员", "检查类型", "开始日期", "完成期限", "场所", "网格", "进度", "检查日期"];
                var types = ListCommunityRecords("inspection-type", includeArchived: true);
                foreach (var plan in ListCommunityRecords("plan", includeArchived: true))
                {
                    if (query.PlanId.Length > 0 && plan.Id != query.PlanId || !Match(plan.Name + JsonText(plan.Data, "inspector"))) continue;
                    var start = JsonText(plan.Data, "from"); var end = JsonText(plan.Data, "to"); var type = JsonText(plan.Data, "typeId");
                    if (string.CompareOrdinal(end, from) < 0 || string.CompareOrdinal(start, to) > 0) continue;
                    var ids = plan.Data.GetProperty("pointIds").EnumerateArray().Select(x => x.GetString()!).Distinct().ToArray();
                    foreach (var id in ids)
                    {
                        var point = GetPointInternal(id);
                        if (query.CommunityId.Length > 0 && point.CommunityId != query.CommunityId) continue;
                        using var checkedQuery = Command("SELECT MAX(inspection_date) FROM inspections WHERE point_id=$point AND result<>'incomplete' AND inspection_date>=$from AND inspection_date<=$to AND ($type='' OR inspection_type_id=$type);", null, ("$point", id), ("$from", start), ("$to", end), ("$type", type));
                        var date = checkedQuery.ExecuteScalar() as string ?? "";
                        var status = plan.Archived ? "archived" : date.Length > 0 ? "completed" : string.CompareOrdinal(Today, end) > 0 ? "overdue" : "pending";
                        if (query.Status.Length > 0 && query.Status != status) continue;
                        rows.Add([plan.Id, plan.Name, JsonText(plan.Data, "inspector"), types.FirstOrDefault(x => x.Id == type)?.Name ?? "", start, end, point.Name, point.CommunityName, status switch { "archived" => "已取消", "completed" => "已检查", "overdue" => "逾期未查", _ => "待检查" }, date]);
                    }
                }
                scope = $"安排期间：{(query.From.Length == 0 ? "不限" : query.From)} 至 {(query.To.Length == 0 ? "不限" : query.To)}；" + scope;
            }
            else throw new ApiException(400, "不支持的台账类型。");
            if (query.Search.Length > 0) scope += "；搜索：" + query.Search;
            if (query.TypeId.Length > 0) scope += "；类型：" + (query.Kind == "inspections" ? ListCommunityRecords("inspection-type", includeArchived: true).FirstOrDefault(x => x.Id == query.TypeId)?.Name : GetPointTypes().FirstOrDefault(x => x.Id == query.TypeId)?.Name);
            return new(title, scope, headers, [.. rows]);
        }
    }
    private static string ReportStatus(string s) => s switch { "" or "all" => "全部", "pending" => "待整改", "feedback" => "待复查", "closed" => "已完成", "overdue" => "已超期", "true" => "已停用", "false" => "使用中", "normal" => "未发现问题", "issues" => "发现问题", "incomplete" => "未完成检查", _ => s };
    public void MapReportApi(WebApplication app)
    {
        static ReportQuery Query(HttpContext c) => new(c.Request.RouteValues["kind"]!.ToString()!, c.Request.Query["from"].ToString(), c.Request.Query["to"].ToString(),c.Request.Query["communityId"].ToString(),c.Request.Query["status"].ToString(),c.Request.Query["q"].ToString(),c.Request.Query["typeId"].ToString(),c.Request.Query["planId"].ToString());
        RequestDelegate preview = c => Api.Write(c, GetReport(Query(c)), ReportJsonContext.Default.ReportDto);
        RequestDelegate download = async c =>
        {
            var report = GetReport(Query(c));
            var format = c.Request.RouteValues["format"]?.ToString();
            if (format is not ("xlsx" or "docx")) throw new ApiException(400, "请选择 Word 或 Excel。");
            using var stream = new MemoryStream();
            if (format == "xlsx") ExcelReport.Write(stream, report); else WordExporter.WriteCommunityReport(stream, report, GetSettings());
            c.Response.ContentType = format == "xlsx" ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            c.Response.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(report.Title + "-" + Today + "." + format)}";
            c.Response.ContentLength = stream.Length; stream.Position = 0; await stream.CopyToAsync(c.Response.Body, c.RequestAborted);
        };
        app.MapGet("/api/reports/{kind}", preview); app.MapGet("/api/reports/{kind}/{format}", download);
    }
}

public static class ExcelReport
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static void Write(Stream destination, ReportDto report)
    {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, true);
        void Text(string path,string text) { using var stream=zip.CreateEntry(path).Open();using var writer=new StreamWriter(stream,new UTF8Encoding(false));writer.Write(text); }
        Text("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
        Text("_rels/.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        Text("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
        Text("xl/workbook.xml", $"<workbook xmlns=\"{S}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"台账\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Text("xl/styles.xml", $"""<styleSheet xmlns="{S}"><fonts count="2"><font><sz val="11"/><name val="Microsoft YaHei"/></font><font><b/><sz val="11"/><name val="Microsoft YaHei"/><color rgb="FFFFFFFF"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF0F6E5A"/></patternFill></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""");
        XElement Row(string[] cells,int row,bool header=false) => new(S+"row",new XAttribute("r",row),cells.Select((text,i)=>new XElement(S+"c",new XAttribute("r",Column(i)+row),new XAttribute("t","inlineStr"),new XAttribute("s",header?1:0),new XElement(S+"is",new XElement(S+"t",new XAttribute(XNamespace.Xml+"space","preserve"),text)))));
        var sheet=new XElement(S+"worksheet",
            new XElement(S+"sheetViews",new XElement(S+"sheetView",new XAttribute("workbookViewId",0),new XElement(S+"pane",new XAttribute("ySplit",3),new XAttribute("topLeftCell","A4"),new XAttribute("activePane","bottomLeft"),new XAttribute("state","frozen")))),
            new XElement(S+"cols",report.Headers.Select((h,i)=>new XElement(S+"col",new XAttribute("min",i+1),new XAttribute("max",i+1),new XAttribute("width",i==0?35:h is "问题" or "整改要求" or "地址"?40:22),new XAttribute("customWidth",1)))),
            new XElement(S+"sheetData",Row([report.Title],1),Row([report.Scope],2),Row(report.Headers,3,true),report.Rows.Select((r,i)=>Row(r,i+4))),
            new XElement(S+"autoFilter",new XAttribute("ref",$"A3:{Column(report.Headers.Length-1)}{report.Rows.Length+3}")));
        using var output=zip.CreateEntry("xl/worksheets/sheet1.xml").Open();sheet.Save(output);
    }
    private static string Column(int index){var value="";for(var n=index+1;n>0;n=(n-1)/26)value=(char)('A'+(n-1)%26)+value;return value;}
}
