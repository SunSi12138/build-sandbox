using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace StreetEcoTool;

public sealed record WordPhoto(PhotoDto Photo, string FilePath);
public sealed record WordIssueHistory(string IssueId, IssueActionDto[] Actions);
public sealed record WordLedger(string From, string To, string CommunityName, IssueDto[] Issues, WordPhoto[] Photos, bool IncludePhotos, WordIssueHistory[]? History = null, string? PreparedDate = null);
public sealed record WordInspectionLedger(string From, string To, string CommunityName, InspectionDto[] Inspections, string PreparedDate);
public sealed record WordCampaignLedger(string From, string To, string CommunityName, CampaignDto[] Campaigns, WordPhoto[] Photos, bool IncludePhotos, string PreparedDate);

/// <summary>Writes an OPC/WordprocessingML package with BCL APIs only. No image or Office SDK is loaded.</summary>
public static class WordExporter
{
    public static void WriteRecord(Stream destination, InspectionDetailDto detail, SettingsDto settings, WordPhoto[] photos)
    {
        using var document = new Document(destination, false, "排查记录单", settings.StreetName);
        document.Paragraph("生态环保排查记录单", "Title");
        WriteInspectionInfo(document, detail.Inspection);
        document.Paragraph("一、发现问题", "Heading1");
        if (detail.Issues.Length == 0) document.Paragraph("本次排查未发现问题。");
        else WriteIssues(document, detail.Issues);
        WriteSitePhotos(document, detail, photos, false);
        if (!string.IsNullOrWhiteSpace(detail.Inspection.Notes))
        {
            document.Paragraph("三、备注", "Heading1");
            document.Paragraph(detail.Inspection.Notes);
        }
    }

    public static void WriteNotice(Stream destination, InspectionDetailDto detail, SettingsDto settings, WordPhoto[] photos)
    {
        using var document = new Document(destination, false, "整改通知单", settings.StreetName);
        var inspection = detail.Inspection;
        if (!string.IsNullOrWhiteSpace(inspection.NoticeNumber)) document.Paragraph("编号：" + inspection.NoticeNumber, align: "right");
        document.Paragraph("生态环保问题整改通知单", "Title");
        document.Paragraph(inspection.CommunityTitle.TrimEnd('：', ':') + "：", indent: false);

        // Each issue keeps its own date in the table; the template uses the last deadline.
        var deadline = detail.Issues.Select(issue => issue.Deadline).Order(StringComparer.Ordinal).LastOrDefault() ?? inspection.Date;
        var template = settings.NoticeTemplate
            .Replace("{日期}", ChineseDate(inspection.Date), StringComparison.Ordinal)
            .Replace("{点位}", inspection.PointName, StringComparison.Ordinal)
            .Replace("{期限}", ChineseDate(deadline), StringComparison.Ordinal)
            .Replace("{街道全称}", settings.StreetName, StringComparison.Ordinal)
            .Replace("{社区}", inspection.CommunityName, StringComparison.Ordinal);
        var parts = template.Split("{问题}", StringSplitOptions.None);
        for (var index = 0; index < parts.Length; index++)
        {
            foreach (var paragraph in parts[index].Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                if (!string.IsNullOrWhiteSpace(paragraph)) document.Paragraph(paragraph.Trim());
            if (index == 0 && parts.Length > 1) WriteIssues(document, detail.Issues);
        }
        if (parts.Length == 1) WriteIssues(document, detail.Issues);
        WriteSignature(document, settings, inspection.Date);
        document.Paragraph("整改情况回执", "Heading1");
        document.Table(null,
        [
            ["整改情况", "\n\n"],
            ["负责人签字及日期", "　　　　　　　　　日期：　　　年　　月　　日"],
            ["责任单位（盖章）", "\n\n"]
        ], [24, 76], labelColumn: true, keepTogether: true);
        WriteSitePhotos(document, detail, photos, true);
    }

    public static void WriteRectificationRecord(Stream destination, InspectionDetailDto detail, SettingsDto settings, WordPhoto[] photos)
    {
        var open = detail.Issues.Count(issue => issue.Status != "closed");
        if (open > 0) throw new ApiException(400, $"尚有 {open} 项问题未闭环");
        if (detail.Issues.Length == 0) throw new ApiException(400, "本次排查无需整改");
        using var document = new Document(destination, false, "整改记录单", settings.StreetName);
        document.Paragraph("生态环保问题整改记录单", "Title");
        var completed = detail.Issues.Select(issue => issue.ClosedDate ?? issue.ReviewDate ?? "").Order(StringComparer.Ordinal).Last();
        WriteInspectionInfo(document, detail.Inspection, completed);
        document.Paragraph("一、整改情况", "Heading1");
        for (var index = 0; index < detail.Issues.Length; index++)
        {
            var issue = detail.Issues[index];
            document.Paragraph($"（{ChineseNumber(index + 1)}）问题 {index + 1}", "Heading2");
            var values = new List<string[]>
            {
                new[] { "问题描述", issue.Description }, new[] { "整改要求", issue.Requirement },
                new[] { "整改期限", issue.Deadline }, new[] { "责任单位", issue.ResponsibleUnit },
                new[] { "整改反馈", WithDate(issue.FeedbackDate, issue.FeedbackNotes) },
                new[] { "复查结论", WithDate(issue.ReviewDate, issue.ReviewConclusion) }
            };
            var actions = detail.IssueDetails?.FirstOrDefault(item => item.Issue.Id == issue.Id)?.Actions ?? [];
            if (actions.Any(action => action.Kind == "return"))
                values.Add(["办理过程", string.Join("\n", actions.OrderBy(action => action.Date, StringComparer.Ordinal)
                    .Select(action => $"{action.Date}　{ActionName(action.Kind)}：{action.Notes}"))]);
            document.Table(null, values, [20, 80], labelColumn: true);
            WriteIssuePhotoTables(document, IssuePhotos(photos, issue));
        }
        var reviewed = detail.Issues.Select(issue => issue.ReviewDate ?? issue.ClosedDate ?? "").Order(StringComparer.Ordinal).Last();
        WriteSignature(document, settings, reviewed);
    }

    public static void WriteCampaignRecord(Stream destination, CampaignDetailDto detail, SettingsDto settings, WordPhoto[] photos)
    {
        using var document = new Document(destination, false, "整治记录单", settings.StreetName);
        var campaign = detail.Campaign;
        document.Paragraph("环境整治记录单", "Title");
        document.Table(null,
        [
            ["整治名称", campaign.Name], ["整治类型", campaign.TypeName], ["所属社区", campaign.CommunityName],
            ["整治地点", campaign.Location], ["整治日期", DateRange(campaign.Date, campaign.EndDate)],
            ["组织单位", campaign.Organizer], ["参加单位及人员", campaign.Participants]
        ], [24, 76], labelColumn: true);
        document.Paragraph("一、整治内容", "Heading1");
        document.Paragraph(campaign.Content);
        var quantities = new List<(string Label, string Value)>();
        if (campaign.ParticipantCount is not null) quantities.Add(("参加人数（人）", Number(campaign.ParticipantCount)));
        if (campaign.VehicleCount is not null) quantities.Add(("出动车辆（辆）", Number(campaign.VehicleCount)));
        if (campaign.GarbageTons is not null) quantities.Add(("清运垃圾（吨）", DecimalNumber(campaign.GarbageTons)));
        if (campaign.AreaSqm is not null) quantities.Add(("清理面积（㎡）", DecimalNumber(campaign.AreaSqm)));
        if (quantities.Count > 0 || !string.IsNullOrWhiteSpace(campaign.Outcome))
        {
            document.Paragraph("二、整治成效", "Heading1");
            if (quantities.Count > 0) document.Table(quantities.Select(item => item.Label).ToArray(),
                [quantities.Select(item => item.Value).ToArray()], Enumerable.Repeat(1, quantities.Count).ToArray(), centeredColumns: Enumerable.Range(0, quantities.Count).ToArray());
            if (!string.IsNullOrWhiteSpace(campaign.Outcome)) document.Paragraph(campaign.Outcome);
        }
        var selected = SortPhotos(photos.Where(photo => photo.Photo.CampaignId == campaign.Id));
        if (selected.Length > 0)
        {
            document.Paragraph("三、整治照片", "Heading1");
            WriteCampaignPhotoTables(document, selected);
        }
        if (!string.IsNullOrWhiteSpace(campaign.Notes))
        {
            document.Paragraph("备注", "Heading1");
            document.Paragraph(campaign.Notes);
        }
    }

    public static void WriteLedger(Stream destination, WordLedger ledger, SettingsDto settings)
    {
        using var document = new Document(destination, true, "整改台账", settings.StreetName);
        document.Paragraph("生态环保问题整改台账", "Title");
        WriteLedgerInfo(document, ledger.From, ledger.To, ledger.CommunityName, settings, ledger.PreparedDate ?? ledger.To);
        var histories = (ledger.History ?? []).ToDictionary(item => item.IssueId, item => item.Actions, StringComparer.Ordinal);
        var rows = ledger.Issues.Select((issue, index) => new[]
        {
            Number(index + 1), issue.InspectionDate, issue.PointName, issue.CommunityName, issue.Description, issue.Deadline, Status(issue.Status),
            HistoryText(histories.GetValueOrDefault(issue.Id) ?? [], true, WithDate(issue.FeedbackDate, issue.FeedbackNotes)),
            HistoryText(histories.GetValueOrDefault(issue.Id) ?? [], false, WithDate(issue.ReviewDate, issue.ReviewConclusion))
        });
        var closed = ledger.Issues.Count(issue => issue.Status == "closed");
        var feedback = ledger.Issues.Count(issue => issue.Status == "feedback");
        var pending = ledger.Issues.Count(issue => issue.Status == "pending");
        var overdue = ledger.Issues.Count(issue => issue.Status != "closed" && issue.Overdue);
        document.Table(["序号", "排查日期", "点位名称", "所属社区", "问题描述", "整改期限", "状态", "反馈情况", "复查结论"], rows,
            [5, 9, 13, 9, 22, 9, 8, 13, 12], centeredColumns: [0, 1, 5, 6],
            summary: $"共 {ledger.Issues.Length} 条，已闭环 {closed}，待复查 {feedback}，待整改 {pending}，其中逾期 {overdue}");
        if (!ledger.IncludePhotos) return;
        var attachments = ledger.Issues.Select((issue, index) => (Issue: issue, Index: index, Photos: IssuePhotos(ledger.Photos, issue)))
            .Where(item => item.Photos.Length > 0).ToArray();
        if (attachments.Length == 0) return;
        document.Paragraph("附：照片", "Heading1", pageBreak: true);
        foreach (var item in attachments)
        {
            document.Paragraph($"（{ChineseNumber(item.Index + 1)}）问题 {item.Index + 1}　{item.Issue.PointName}", "Heading2");
            WriteIssuePhotoTables(document, item.Photos);
        }
    }

    public static void WriteInspectionLedger(Stream destination, WordInspectionLedger ledger, SettingsDto settings)
    {
        using var document = new Document(destination, true, "排查台账", settings.StreetName);
        document.Paragraph("生态环保排查台账", "Title");
        WriteLedgerInfo(document, ledger.From, ledger.To, ledger.CommunityName, settings, ledger.PreparedDate);
        var rows = ledger.Inspections.Select((inspection, index) => new[]
        {
            Number(index + 1), inspection.Date, inspection.PointName, inspection.CommunityName, inspection.TypeName, inspection.Inspector,
            inspection.Result == "normal" ? "正常" : "发现问题", Number(inspection.IssueCount), RectifyStatus(inspection), inspection.NoticeNumber
        });
        var normal = ledger.Inspections.Count(inspection => inspection.Result == "normal");
        document.Table(["序号", "排查日期", "点位名称", "所属社区", "点位类型", "排查人", "排查结果", "问题数", "整改情况", "通知单编号"],
            rows, [5, 10, 18, 11, 10, 8, 9, 7, 12, 10], centeredColumns: [0, 1, 6, 7, 8],
            summary: $"共 {ledger.Inspections.Length} 次排查，正常 {normal}，发现问题 {ledger.Inspections.Length - normal}；问题共 {ledger.Inspections.Sum(item => item.IssueCount)} 项，已闭环 {ledger.Inspections.Sum(item => item.ClosedCount)}");
    }

    public static void WriteCampaignLedger(Stream destination, WordCampaignLedger ledger, SettingsDto settings)
    {
        using var document = new Document(destination, true, "整治台账", settings.StreetName);
        document.Paragraph("环境整治台账", "Title");
        WriteLedgerInfo(document, ledger.From, ledger.To, ledger.CommunityName, settings, ledger.PreparedDate);
        var rows = ledger.Campaigns.Select((campaign, index) => new[]
        {
            Number(index + 1), DateRange(campaign.Date, campaign.EndDate), campaign.Name, campaign.TypeName, campaign.CommunityName,
            campaign.Location, Number(campaign.ParticipantCount), Number(campaign.VehicleCount), DecimalNumber(campaign.GarbageTons),
            DecimalNumber(campaign.AreaSqm), CampaignSummary(campaign)
        });
        document.Table(["序号", "整治日期", "整治名称", "类型", "所属社区", "整治地点", "参加人数", "出动车辆", "清运垃圾（吨）", "清理面积（㎡）", "整治内容及成效"],
            rows, [4, 9, 14, 8, 8, 11, 6, 6, 7, 7, 20], centeredColumns: [0, 1], rightAlignedColumns: [6, 7, 8, 9],
            summary: $"共 {ledger.Campaigns.Length} 次整治，累计参加 {ledger.Campaigns.Sum(item => (long)(item.ParticipantCount ?? 0))} 人、出动车辆 {ledger.Campaigns.Sum(item => (long)(item.VehicleCount ?? 0))} 辆、清运垃圾 {DecimalNumber(ledger.Campaigns.Sum(item => item.GarbageTons ?? 0))} 吨、清理面积 {DecimalNumber(ledger.Campaigns.Sum(item => item.AreaSqm ?? 0))} ㎡");
        if (!ledger.IncludePhotos) return;
        var attachments = ledger.Campaigns.Select((campaign, index) => (Campaign: campaign, Index: index,
            Photos: SortPhotos(ledger.Photos.Where(photo => photo.Photo.CampaignId == campaign.Id)))).Where(item => item.Photos.Length > 0).ToArray();
        if (attachments.Length == 0) return;
        document.Paragraph("附：照片", "Heading1", pageBreak: true);
        foreach (var item in attachments)
        {
            document.Paragraph($"（{ChineseNumber(item.Index + 1)}）{item.Campaign.Name}", "Heading2");
            WriteCampaignPhotoTables(document, item.Photos);
        }
    }

    public static string FileName(string kind, string pointName, string isoDate)
    {
        static string Safe(string value, int maximumBytes)
        {
            var text = new string(value.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
            var result = new StringBuilder();
            var bytes = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > maximumBytes) break;
                result.Append(rune.ToString());
                bytes += rune.Utf8SequenceLength;
            }
            return result.Length == 0 ? "未命名" : result.ToString();
        }
        return $"{Safe(kind, 48)}_{Safe(pointName, 150)}_{Safe(isoDate.Replace("-", "", StringComparison.Ordinal), 24)}.docx";
    }

    private static void WriteInspectionInfo(Document document, InspectionDto inspection, string? completed = null)
    {
        var rows = new List<Cell[]>
        {
            [new("点位名称", Header: true), new(inspection.PointName), new("点位类型", Header: true), new(inspection.TypeName)],
            [new("所属社区", Header: true), new(inspection.CommunityName), new("排查日期", Header: true), new(inspection.Date, Align: "center")],
            [new("详细地址", Header: true), new(inspection.Address, Span: 3)],
            [new("排查人", Header: true), new(inspection.Inspector), new("排查结果", Header: true), new(inspection.Result == "normal" ? "正常" : "发现问题", Align: "center")]
        };
        var contact = string.Join("　", new[] { inspection.Contact, inspection.Phone }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (contact.Length > 0) rows.Add([new("联系人及电话", Header: true), new(contact, Span: 3)]);
        if (completed is not null) rows.Add([new("通知单编号", Header: true), new(inspection.NoticeNumber), new("整改完成日期", Header: true), new(completed, Align: "center")]);
        document.CellTable(rows, [18, 32, 18, 32]);
    }

    private static void WriteLedgerInfo(Document document, string from, string to, string community, SettingsDto settings, string preparedDate) =>
        document.CellTable([
            [new("统计范围", Header: true), new(ChineseDate(from) + "至" + ChineseDate(to)), new("所属社区", Header: true), new(community == "全部社区" ? "全部" : community)],
            [new("制表单位", Header: true), new(string.IsNullOrWhiteSpace(settings.IssuingUnit) ? settings.StreetName : settings.IssuingUnit), new("制表日期", Header: true), new(preparedDate, Align: "center")]
        ], [12, 48, 12, 28]);

    private static void WriteSignature(Document document, SettingsDto settings, string date)
    {
        var unit = string.IsNullOrWhiteSpace(settings.IssuingUnit) ? settings.StreetName : settings.IssuingUnit;
        if (!string.IsNullOrWhiteSpace(unit)) document.Paragraph(unit, align: "right");
        if (!string.IsNullOrWhiteSpace(date)) document.Paragraph(ChineseDate(date), align: "right");
    }

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string DecimalNumber(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
    private static string DateRange(string from, string? to) => string.IsNullOrEmpty(to) || to == from ? from : from + " 至 " + to;
    private static string ChineseDate(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.Year.ToString(CultureInfo.InvariantCulture) + "年" + date.Month.ToString(CultureInfo.InvariantCulture) + "月" + date.Day.ToString(CultureInfo.InvariantCulture) + "日" : value;
    private static string ChineseNumber(int value)
    {
        string[] digits = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九"];
        if (value is > 0 and < 10) return digits[value];
        if (value is >= 10 and < 100) return (value >= 20 ? digits[value / 10] : "") + "十" + (value % 10 > 0 ? digits[value % 10] : "");
        return value.ToString(CultureInfo.InvariantCulture);
    }
    private static string WithDate(string? date, string text) => string.IsNullOrWhiteSpace(date) ? text : date + "\n" + text;
    private static string ActionName(string kind) => kind switch { "feedback" => "反馈", "review" => "复查", "return" => "退回", _ => kind };
    private static string HistoryText(IssueActionDto[] actions, bool feedback, string current)
    {
        var selected = actions.Where(action => feedback ? action.Kind == "feedback" : action.Kind is "review" or "return").ToArray();
        if (selected.Length == 0) return current;
        var prefix = feedback && (selected.Length > 1 || actions.Any(action => action.Kind == "return")) ? "历次反馈：\n" : "";
        return prefix + string.Join("\n\n", selected.Select(action =>
            (feedback ? "" : action.Kind == "return" ? "退回 " : "复查 ") + WithDate(action.Date, action.Notes)));
    }
    private static string Status(string status) => status switch { "pending" => "待整改", "feedback" => "社区已反馈", "closed" => "已复查闭环", _ => status };
    private static string RectifyStatus(InspectionDto inspection) => inspection.RectifyStatus switch
    {
        "pending" => "待整改", "feedback" => "待复查", "closed" => "已闭环", _ => inspection.Result == "normal" ? "无需整改" : inspection.OpenIssueCount == 0 ? "已闭环" : "待整改"
    };
    private static string CampaignSummary(CampaignDto campaign)
    {
        static string Sentence(string text)
        {
            var trimmed = text.Trim();
            var end = trimmed.IndexOfAny(['。', '！', '？', '!', '?', '\n', '\r']);
            return end < 0 ? trimmed : trimmed[..(end + 1)].Trim();
        }
        var text = string.Join("　", new[] { Sentence(campaign.Content), Sentence(campaign.Outcome) }.Where(value => value.Length > 0));
        var runes = text.EnumerateRunes().ToArray();
        return runes.Length <= 60 ? text : string.Concat(runes.Take(60).Select(rune => rune.ToString())) + "…";
    }
    private static void WriteIssues(Document document, IssueDto[] issues) => document.Table(
        ["序号", "问题描述", "整改要求", "整改期限", "责任单位"],
        issues.Select((issue, index) => new[] { Number(index + 1), issue.Description, issue.Requirement, issue.Deadline, issue.ResponsibleUnit }),
        [7, 31, 27, 16, 19], centeredColumns: [0, 3]);

    private static WordPhoto[] SortPhotos(IEnumerable<WordPhoto> photos) => photos.OrderBy(photo => photo.Photo.UploadedAt, StringComparer.Ordinal)
        .ThenBy(photo => photo.Photo.Id, StringComparer.Ordinal).ToArray();
    private static WordPhoto[] IssuePhotos(WordPhoto[] photos, IssueDto issue) => SortPhotos(photos.Where(photo => photo.Photo.IssueId == issue.Id ||
        (photo.Photo.InspectionId == issue.InspectionId && photo.Photo.Phase == "site")));
    private static void WriteIssuePhotoTables(Document document, WordPhoto[] photos)
    {
        document.Comparison(photos.Where(photo => photo.Photo.Phase == "site").ToArray(), photos.Where(photo => photo.Photo.Phase == "after").ToArray(), "整改前", "整改后");
        document.Gallery(photos.Where(photo => photo.Photo.Phase == "review").Select(photo => (photo, "")).ToArray(), "复查");
    }
    private static void WriteCampaignPhotoTables(Document document, WordPhoto[] photos)
    {
        document.Comparison(photos.Where(photo => photo.Photo.Phase == "before").ToArray(), photos.Where(photo => photo.Photo.Phase == "after").ToArray(), "整治前", "整治后");
        document.Gallery(photos.Where(photo => photo.Photo.Phase == "process").Select(photo => (photo, "")).ToArray(), "整治中");
    }
    private static void WriteSitePhotos(Document document, InspectionDetailDto detail, WordPhoto[] photos, bool attachment)
    {
        var selected = photos.Where(photo => photo.Photo.Phase == "site")
            .OrderBy(photo => Array.FindIndex(detail.Issues, issue => issue.Id == photo.Photo.IssueId))
            .ThenBy(photo => photo.Photo.UploadedAt, StringComparer.Ordinal).ThenBy(photo => photo.Photo.Id, StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) return;
        document.Paragraph(attachment ? "附件：现场问题照片" : "二、现场照片", "Heading1", pageBreak: attachment);
        document.Gallery(selected.Select(photo =>
        {
            var issueIndex = Array.FindIndex(detail.Issues, issue => issue.Id == photo.Photo.IssueId);
            return (photo, issueIndex < 0 ? "" : $"问题 {issueIndex + 1}");
        }).ToArray());
    }

    private sealed record Cell(string Text, int Span = 1, bool Header = false, string? Align = null, bool Bold = false);

    private sealed class Document : IDisposable
    {
        private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private const string Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        private const string Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
        private static readonly XmlWriterSettings XmlSettings = new() { Encoding = new UTF8Encoding(false), CloseOutput = true, Indent = false };
        private readonly ZipArchive _zip;
        private readonly XmlWriter _xml;
        private readonly bool _landscape;
        private readonly string _title;
        private readonly string _author;
        private readonly List<Asset> _assets = [];
        private int _drawingId;
        private int Width => _landscape ? 14570 : 9072;
        private sealed record Asset(string Id, string Relationship, string Target, string FilePath, JpegDimensions Dimensions);

        public Document(Stream destination, bool landscape, string title, string author)
        {
            _landscape = landscape;
            _title = title;
            _author = author;
            _zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
            _xml = XmlWriter.Create(_zip.CreateEntry("word/document.xml", CompressionLevel.Optimal).Open(), XmlSettings);
            _xml.WriteStartDocument();
            _xml.WriteStartElement("w", "document", W);
            _xml.WriteAttributeString("xmlns", "r", null, R);
            _xml.WriteAttributeString("xmlns", "wp", null, Wp);
            _xml.WriteAttributeString("xmlns", "a", null, A);
            _xml.WriteAttributeString("xmlns", "pic", null, Pic);
            Start("body");
        }

        private void Start(string name) => _xml.WriteStartElement("w", name, W);
        private void End() => _xml.WriteEndElement();
        private void Attr(string name, string value) => _xml.WriteAttributeString("w", name, W, value);
        private void Value(string name, string value) { Start(name); Attr("val", value); End(); }

        public void Paragraph(string text, string style = "Normal", string? align = null, bool? indent = null, bool pageBreak = false)
        {
            Start("p");
            Start("pPr");
            Value("pStyle", style);
            if (pageBreak) Value("pageBreakBefore", "1");
            Spacing(style);
            Start("ind"); Attr("firstLine", (indent ?? (style == "Normal" && align is null)) ? "640" : "0"); End();
            if (align is not null) Value("jc", align);
            End();
            TextRun(text);
            End();
        }

        private void Spacing(string style)
        {
            var (before, after) = style switch { "Title" => (0, 240), "Heading1" => (240, 120), "Heading2" => (120, 60), _ => (0, 0) };
            Start("spacing"); Attr("before", before.ToString(CultureInfo.InvariantCulture)); Attr("after", after.ToString(CultureInfo.InvariantCulture));
            Attr("line", "240"); Attr("lineRule", "auto"); End();
        }

        private void TextRun(string text, int? fontSize = null, bool bold = false)
        {
            Start("r");
            if (fontSize is not null || bold)
            {
                Start("rPr");
                if (bold) Value("b", "1");
                if (fontSize is not null) { Value("sz", fontSize.Value.ToString(CultureInfo.InvariantCulture)); Value("szCs", fontSize.Value.ToString(CultureInfo.InvariantCulture)); }
                End();
            }
            var lines = XmlText(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0) { Start("br"); End(); }
                Start("t");
                _xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                _xml.WriteString(lines[index]);
                End();
            }
            End();
        }

        private static string XmlText(string value)
        {
            var text = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (XmlConvert.IsXmlChar(c)) text.Append(c);
                else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { text.Append(c); text.Append(value[++i]); }
            }
            return text.ToString();
        }

        public void Table(string[]? headers, IEnumerable<string[]> rows, int[] weights, int fontSize = 21,
            bool labelColumn = false, bool keepTogether = false, int[]? centeredColumns = null, int[]? rightAlignedColumns = null, string? summary = null)
        {
            var widths = TableStart(weights);
            if (headers is not null) CellRow(headers.Select(text => new Cell(text, Header: true)).ToArray(), widths, header: true, keepWithNext: keepTogether, fontSize: fontSize);
            if (keepTogether)
            {
                // Only the three-row receipt is linked across rows. Ordinary and image rows remain independently pageable.
                var group = rows.ToArray();
                for (var index = 0; index < group.Length; index++) Write(group[index], index + 1 < group.Length);
            }
            else foreach (var row in rows) Write(row, false);
            if (summary is not null) CellRow([new(summary, Span: widths.Length, Align: "left", Bold: true)], widths);
            End();
            Paragraph("", "TableGap");
            void Write(string[] values, bool keepWithNext)
            {
                var cells = widths.Select((_, column) => new Cell(column < values.Length ? values[column] : "",
                    Header: labelColumn && column == 0,
                    Align: rightAlignedColumns?.Contains(column) == true ? "right" : centeredColumns?.Contains(column) == true ? "center" : null)).ToArray();
                CellRow(cells, widths, keepWithNext: keepWithNext, fontSize: fontSize);
            }
        }

        public void CellTable(IEnumerable<Cell[]> rows, int[] weights)
        {
            var widths = TableStart(weights);
            foreach (var row in rows) CellRow(row, widths);
            End(); Paragraph("", "TableGap");
        }

        private int[] TableStart(int[] weights)
        {
            var total = weights.Sum();
            var widths = weights.Select(weight => Width * weight / total).ToArray();
            widths[^1] += Width - widths.Sum();
            Start("tbl");
            Start("tblPr");
            Start("tblW"); Attr("w", Width.ToString(CultureInfo.InvariantCulture)); Attr("type", "dxa"); End();
            Value("jc", "center");
            Start("tblBorders");
            foreach (var edge in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            {
                Start(edge); Attr("val", "single"); Attr("sz", "4"); Attr("color", "000000"); End();
            }
            End();
            Start("tblLayout"); Attr("type", "fixed"); End();
            Start("tblCellMar");
            foreach (var edge in new[] { "top", "left", "bottom", "right" })
            { Start(edge); Attr("w", edge is "top" or "bottom" ? "50" : "100"); Attr("type", "dxa"); End(); }
            End(); End();
            Start("tblGrid");
            foreach (var width in widths) { Start("gridCol"); Attr("w", width.ToString(CultureInfo.InvariantCulture)); End(); }
            End();
            return widths;
        }

        private void CellRow(Cell[] cells, int[] widths, bool header = false, bool keepWithNext = false, int fontSize = 21)
        {
            if (cells.Sum(cell => cell.Span) != widths.Length) throw new InvalidDataException("表格列宽不正确。");
            Start("tr"); Start("trPr"); Value("cantSplit", "1");
            if (header) Value("tblHeader", "1");
            End();
            var column = 0;
            foreach (var cell in cells)
            {
                var width = widths.Skip(column).Take(cell.Span).Sum();
                column += cell.Span;
                CellStart(width, cell.Header || header, cell.Span);
                Start("p"); Start("pPr"); Value("pStyle", cell.Header || header ? "TableHeader" : "TableText");
                if (keepWithNext) Value("keepNext", "1");
                Spacing("TableText");
                Start("ind"); Attr("firstLine", "0"); End();
                Value("jc", cell.Header || header ? "center" : cell.Align ?? "left");
                End();
                TextRun(cell.Text, fontSize, cell.Bold);
                End(); End();
            }
            End();
        }

        private void CellStart(int width, bool shaded = false, int span = 1)
        {
            Start("tc"); Start("tcPr");
            Start("tcW"); Attr("w", width.ToString(CultureInfo.InvariantCulture)); Attr("type", "dxa"); End();
            if (span > 1) Value("gridSpan", span.ToString(CultureInfo.InvariantCulture));
            if (shaded) { Start("shd"); Attr("val", "clear"); Attr("fill", "F2F2F2"); End(); }
            Value("vAlign", "center");
            End();
        }

        public void Gallery((WordPhoto Photo, string Caption)[] photos, string? heading = null)
        {
            if (photos.Length == 0) return;
            var widths = TableStart([1, 1]);
            if (heading is not null) CellRow([new(heading, Span: 2, Header: true)], widths, header: true);
            for (var index = 0; index < photos.Length; index += 2)
            {
                Start("tr"); Start("trPr"); Value("cantSplit", "1"); End();
                for (var column = 0; column < 2; column++)
                {
                    CellStart(widths[column]);
                    if (index + column < photos.Length)
                    {
                        var item = photos[index + column];
                        PhotoCell(item.Photo, item.Caption, widths[column] - 200);
                    }
                    else Paragraph("", "Photo");
                    End();
                }
                End();
            }
            End(); Paragraph("", "TableGap");
        }

        public void Comparison(WordPhoto[] before, WordPhoto[] after, string beforeHeading, string afterHeading)
        {
            var count = Math.Max(before.Length, after.Length);
            if (count == 0) return;
            var widths = TableStart([1, 1]);
            CellRow([new(beforeHeading, Header: true), new(afterHeading, Header: true)], widths, header: true);
            for (var index = 0; index < count; index++)
            {
                Start("tr"); Start("trPr"); Value("cantSplit", "1"); End();
                for (var column = 0; column < 2; column++)
                {
                    CellStart(widths[column]);
                    var photos = column == 0 ? before : after;
                    if (index < photos.Length) PhotoCell(photos[index], "", widths[column] - 200);
                    else Paragraph("", "Photo");
                    End();
                }
                End();
            }
            End(); Paragraph("", "TableGap");
        }

        private void PhotoCell(WordPhoto photo, string caption, int maximumWidth)
        {
            if (caption.Length > 0) Paragraph(caption, "Caption", "center");
            var asset = _assets.FirstOrDefault(item => item.Id == photo.Photo.Id);
            if (asset is null)
            {
                var length = new FileInfo(photo.FilePath).Length;
                if (length is <= 0 or > PhotoStorage.MaximumUploadBytes) throw new InvalidDataException("导出照片大小不正确，请检查照片文件。");
                // Parse the actual JPEG SOF, never infer dimensions from a file name or load an image library.
                var dimensions = JpegMetadata.Read(File.ReadAllBytes(photo.FilePath));
                var number = _assets.Count + 1;
                asset = new(photo.Photo.Id, "rPhoto" + number, "media/image" + number + ".jpg", photo.FilePath, dimensions);
                _assets.Add(asset);
            }
            var scale = Math.Min(maximumWidth / (double)asset.Dimensions.Width, (_landscape ? 3400 : 5100) / (double)asset.Dimensions.Height);
            var cx = Math.Max(1, (long)Math.Round(asset.Dimensions.Width * scale * 635));
            var cy = Math.Max(1, (long)Math.Round(asset.Dimensions.Height * scale * 635));
            Start("p"); Start("pPr"); Value("pStyle", "Photo"); Spacing("Photo"); Start("ind"); Attr("firstLine", "0"); End(); Value("jc", "center"); End();
            Start("r"); Start("drawing");
            _xml.WriteStartElement("wp", "inline", Wp);
            foreach (var margin in new[] { "distT", "distB", "distL", "distR" }) _xml.WriteAttributeString(margin, "0");
            _xml.WriteStartElement("wp", "extent", Wp); Size(cx, cy); End();
            _xml.WriteStartElement("wp", "docPr", Wp); _xml.WriteAttributeString("id", (++_drawingId).ToString(CultureInfo.InvariantCulture)); _xml.WriteAttributeString("name", "照片 " + _drawingId); _xml.WriteAttributeString("descr", XmlText(caption)); End();
            _xml.WriteStartElement("wp", "cNvGraphicFramePr", Wp);
            _xml.WriteStartElement("a", "graphicFrameLocks", A); _xml.WriteAttributeString("noChangeAspect", "1"); End(); End();
            _xml.WriteStartElement("a", "graphic", A);
            _xml.WriteStartElement("a", "graphicData", A); _xml.WriteAttributeString("uri", Pic);
            _xml.WriteStartElement("pic", "pic", Pic);
            _xml.WriteStartElement("pic", "nvPicPr", Pic);
            _xml.WriteStartElement("pic", "cNvPr", Pic); _xml.WriteAttributeString("id", _drawingId.ToString(CultureInfo.InvariantCulture)); _xml.WriteAttributeString("name", "image" + _drawingId + ".jpg"); End();
            _xml.WriteStartElement("pic", "cNvPicPr", Pic); End(); End();
            _xml.WriteStartElement("pic", "blipFill", Pic);
            _xml.WriteStartElement("a", "blip", A); _xml.WriteAttributeString("r", "embed", R, asset.Relationship); End();
            _xml.WriteStartElement("a", "stretch", A); _xml.WriteStartElement("a", "fillRect", A); End(); End(); End();
            _xml.WriteStartElement("pic", "spPr", Pic);
            _xml.WriteStartElement("a", "xfrm", A);
            _xml.WriteStartElement("a", "off", A); _xml.WriteAttributeString("x", "0"); _xml.WriteAttributeString("y", "0"); End();
            _xml.WriteStartElement("a", "ext", A); Size(cx, cy); End(); End();
            _xml.WriteStartElement("a", "prstGeom", A); _xml.WriteAttributeString("prst", "rect"); _xml.WriteStartElement("a", "avLst", A); End(); End();
            End(); End(); End(); End(); End(); End(); End(); End();
        }

        private void Size(long cx, long cy)
        {
            _xml.WriteAttributeString("cx", cx.ToString(CultureInfo.InvariantCulture));
            _xml.WriteAttributeString("cy", cy.ToString(CultureInfo.InvariantCulture));
        }

        public void Dispose()
        {
            try
            {
                Start("sectPr");
                Start("footerReference"); Attr("type", "default"); _xml.WriteAttributeString("r", "id", R, "rFooter"); End();
                Start("pgSz"); Attr("w", _landscape ? "16838" : "11906"); Attr("h", _landscape ? "11906" : "16838");
                if (_landscape) Attr("orient", "landscape"); End();
                Start("pgMar"); Attr("top", _landscape ? "1134" : "1417"); Attr("right", _landscape ? "1134" : "1417"); Attr("bottom", _landscape ? "1134" : "1417"); Attr("left", _landscape ? "1134" : "1417"); Attr("header", "567"); Attr("footer", "567"); Attr("gutter", "0"); End();
                End(); End(); End(); _xml.WriteEndDocument(); _xml.Dispose();
                WritePart("[Content_Types].xml", ContentTypes);
                WritePart("_rels/.rels", """
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rDocument" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/><Relationship Id="rCore" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>
                    """);
                WritePart("word/styles.xml", Styles);
                WritePart("word/settings.xml", """
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <w:settings xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:defaultTabStop w:val="720"/></w:settings>
                    """);
                WritePart("word/fontTable.xml", Fonts);
                WritePart("word/footer1.xml", """
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <w:ftr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:p><w:pPr><w:pStyle w:val="Footer"/><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/><w:ind w:firstLine="0"/><w:jc w:val="center"/></w:pPr><w:r><w:t xml:space="preserve">— </w:t></w:r><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r><w:r><w:t xml:space="preserve"> —</w:t></w:r></w:p></w:ftr>
                    """);
                using (var core = XmlWriter.Create(_zip.CreateEntry("docProps/core.xml").Open(), XmlSettings))
                {
                    core.WriteStartDocument();
                    core.WriteStartElement("cp", "coreProperties", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties");
                    core.WriteElementString("dc", "title", "http://purl.org/dc/elements/1.1/", _title);
                    core.WriteElementString("dc", "creator", "http://purl.org/dc/elements/1.1/", XmlText(_author));
                    core.WriteEndElement(); core.WriteEndDocument();
                }
                using (var relations = XmlWriter.Create(_zip.CreateEntry("word/_rels/document.xml.rels").Open(), XmlSettings))
                {
                    relations.WriteStartDocument(); relations.WriteStartElement("Relationships", Rel);
                    void Relationship(string id, string kind, string target)
                    {
                        relations.WriteStartElement("Relationship", Rel); relations.WriteAttributeString("Id", id);
                        relations.WriteAttributeString("Type", R + "/" + kind); relations.WriteAttributeString("Target", target); relations.WriteEndElement();
                    }
                    Relationship("rStyles", "styles", "styles.xml"); Relationship("rSettings", "settings", "settings.xml");
                    Relationship("rFonts", "fontTable", "fontTable.xml"); Relationship("rFooter", "footer", "footer1.xml");
                    foreach (var asset in _assets) Relationship(asset.Relationship, "image", asset.Target);
                    relations.WriteEndElement(); relations.WriteEndDocument();
                }
                foreach (var asset in _assets)
                {
                    using var source = File.OpenRead(asset.FilePath);
                    using var target = _zip.CreateEntry("word/" + asset.Target, CompressionLevel.NoCompression).Open();
                    source.CopyTo(target);
                }
            }
            finally { _xml.Dispose(); _zip.Dispose(); }
        }

        private void WritePart(string name, string xml)
        {
            using var writer = new StreamWriter(_zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }

        private const string ContentTypes = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="jpg" ContentType="image/jpeg"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/><Override PartName="/word/settings.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"/><Override PartName="/word/fontTable.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.fontTable+xml"/><Override PartName="/word/footer1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/></Types>
            """;

        private const string Fonts = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:fonts xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:font w:name="方正小标宋简体"><w:altName w:val="宋体"/><w:family w:val="roman"/><w:pitch w:val="variable"/></w:font><w:font w:name="仿宋"><w:altName w:val="宋体"/><w:family w:val="roman"/><w:pitch w:val="variable"/></w:font><w:font w:name="黑体"><w:altName w:val="微软雅黑"/><w:family w:val="swiss"/><w:pitch w:val="variable"/></w:font><w:font w:name="宋体"><w:family w:val="roman"/><w:pitch w:val="variable"/></w:font><w:font w:name="微软雅黑"><w:family w:val="swiss"/><w:pitch w:val="variable"/></w:font></w:fonts>
            """;

        // Paragraph spacing is also written directly, including image paragraphs and empty table gaps.
        private const string Styles = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="仿宋" w:hAnsi="仿宋" w:eastAsia="仿宋" w:cs="仿宋" w:hint="eastAsia"/><w:color w:val="000000"/><w:sz w:val="32"/><w:szCs w:val="32"/><w:lang w:val="zh-CN" w:eastAsia="zh-CN"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
              <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/><w:pPr><w:widowControl/><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/><w:ind w:firstLine="640"/></w:pPr><w:rPr><w:rFonts w:ascii="仿宋" w:hAnsi="仿宋" w:eastAsia="仿宋" w:cs="仿宋" w:hint="eastAsia"/><w:b w:val="0"/><w:sz w:val="32"/><w:szCs w:val="32"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="0" w:after="240" w:line="240" w:lineRule="auto"/><w:ind w:firstLine="0"/><w:jc w:val="center"/></w:pPr><w:rPr><w:rFonts w:ascii="方正小标宋简体" w:hAnsi="方正小标宋简体" w:eastAsia="方正小标宋简体" w:cs="方正小标宋简体" w:hint="eastAsia"/><w:b w:val="0"/><w:sz w:val="44"/><w:szCs w:val="44"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="240" w:after="120" w:line="240" w:lineRule="auto"/><w:ind w:firstLine="0"/><w:jc w:val="left"/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:rFonts w:ascii="黑体" w:hAnsi="黑体" w:eastAsia="黑体" w:cs="黑体" w:hint="eastAsia"/><w:b w:val="0"/><w:sz w:val="32"/><w:szCs w:val="32"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="120" w:after="60" w:line="240" w:lineRule="auto"/><w:ind w:firstLine="0"/><w:jc w:val="left"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:rFonts w:ascii="仿宋" w:hAnsi="仿宋" w:eastAsia="仿宋" w:cs="仿宋" w:hint="eastAsia"/><w:b w:val="1"/><w:sz w:val="32"/><w:szCs w:val="32"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="TableHeader"><w:name w:val="表格表头"/><w:basedOn w:val="Normal"/><w:pPr><w:widowControl w:val="0"/><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/><w:ind w:firstLine="0"/><w:jc w:val="center"/></w:pPr><w:rPr><w:rFonts w:ascii="黑体" w:hAnsi="黑体" w:eastAsia="黑体" w:cs="黑体" w:hint="eastAsia"/><w:b w:val="0"/><w:sz w:val="21"/><w:szCs w:val="21"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="TableText"><w:name w:val="表格正文"/><w:basedOn w:val="Normal"/><w:pPr><w:widowControl w:val="0"/><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/><w:ind w:firstLine="0"/><w:jc w:val="left"/></w:pPr><w:rPr><w:rFonts w:ascii="仿宋" w:hAnsi="仿宋" w:eastAsia="仿宋" w:cs="仿宋" w:hint="eastAsia"/><w:b w:val="0"/><w:sz w:val="21"/><w:szCs w:val="21"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="TableGap"><w:name w:val="表格间距"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/><w:ind w:firstLine="0"/></w:pPr><w:rPr><w:rFonts w:ascii="仿宋" w:hAnsi="仿宋" w:eastAsia="仿宋" w:cs="仿宋" w:hint="eastAsia"/><w:sz w:val="2"/><w:szCs w:val="2"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="Caption"><w:name w:val="照片标注"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/><w:ind w:firstLine="0"/><w:jc w:val="center"/></w:pPr><w:rPr><w:rFonts w:ascii="仿宋" w:hAnsi="仿宋" w:eastAsia="仿宋" w:cs="仿宋" w:hint="eastAsia"/><w:sz w:val="21"/><w:szCs w:val="21"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="Photo"><w:name w:val="照片"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/><w:ind w:firstLine="0"/><w:jc w:val="center"/></w:pPr><w:rPr><w:rFonts w:ascii="仿宋" w:hAnsi="仿宋" w:eastAsia="仿宋" w:cs="仿宋" w:hint="eastAsia"/><w:sz w:val="2"/><w:szCs w:val="2"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="Footer"><w:name w:val="footer"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/><w:ind w:firstLine="0"/><w:jc w:val="center"/></w:pPr><w:rPr><w:rFonts w:ascii="宋体" w:hAnsi="宋体" w:eastAsia="宋体" w:cs="宋体" w:hint="eastAsia"/><w:sz w:val="18"/><w:szCs w:val="18"/></w:rPr></w:style>
            </w:styles>
            """;
    }
}
