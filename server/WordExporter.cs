using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace StreetEcoTool;

public sealed record WordPhoto(PhotoDto Photo, string FilePath);
public sealed record WordLedger(string From, string To, string CommunityName, IssueDto[] Issues, WordPhoto[] Photos, bool IncludePhotos);

/// <summary>Writes an OPC/WordprocessingML package with BCL APIs only. No image or Office SDK is loaded.</summary>
public static class WordExporter
{
    public static void WriteRecord(Stream destination, InspectionDetailDto detail, SettingsDto settings, WordPhoto[] photos)
    {
        using var document = new Document(destination, false, "排查记录单");
        var inspection = detail.Inspection;
        document.Paragraph("生态环保排查记录单", "Title");
        if (settings.StreetName.Length > 0) document.Paragraph(settings.StreetName, "Meta", "center");
        document.Table(null,
        [
            ["点位", inspection.PointName], ["社区", inspection.CommunityName], ["类型", inspection.TypeName],
            ["详细地址", inspection.Address], ["排查日期", inspection.Date], ["排查人", inspection.Inspector],
            ["排查结果", inspection.Result == "normal" ? "正常" : "发现问题"]
        ], [18, 82], labelColumn: true);
        document.Paragraph("问题清单", "Heading1");
        if (detail.Issues.Length == 0) document.Paragraph("本次排查结果正常，未发现需要整改的问题。");
        else WriteIssues(document, detail.Issues);
        WriteSitePhotos(document, detail, photos, false);
        document.Paragraph("备注", "Heading1");
        document.Paragraph(string.IsNullOrWhiteSpace(inspection.Notes) ? "无。" : inspection.Notes);
    }

    public static void WriteNotice(Stream destination, InspectionDetailDto detail, SettingsDto settings, WordPhoto[] photos)
    {
        using var document = new Document(destination, false, "整改通知单");
        var inspection = detail.Inspection;
        if (!string.IsNullOrWhiteSpace(inspection.NoticeNumber)) document.Paragraph("编号：" + inspection.NoticeNumber, "Meta", "right");
        document.Paragraph("生态环保问题整改通知单", "Title");
        document.Paragraph(inspection.CommunityTitle.TrimEnd('：', ':') + "：");

        // Each issue keeps its own date in the table. The template's overall date is the last deadline.
        var deadline = detail.Issues.Select(issue => issue.Deadline).Order(StringComparer.Ordinal).LastOrDefault() ?? inspection.Date;
        var template = settings.NoticeTemplate
            .Replace("{日期}", inspection.Date, StringComparison.Ordinal)
            .Replace("{点位}", inspection.PointName, StringComparison.Ordinal)
            .Replace("{期限}", deadline, StringComparison.Ordinal)
            .Replace("{街道全称}", settings.StreetName, StringComparison.Ordinal)
            .Replace("{社区}", inspection.CommunityName, StringComparison.Ordinal);
        var parts = template.Split("{问题}", StringSplitOptions.None);
        for (var index = 0; index < parts.Length; index++)
        {
            foreach (var paragraph in parts[index].Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                if (!string.IsNullOrWhiteSpace(paragraph)) document.Paragraph(paragraph.Trim(), indent: true);
            if (index == 0 && parts.Length > 1) WriteIssues(document, detail.Issues);
        }
        if (parts.Length == 1)
        {
            document.Paragraph("问题清单及整改要求", "Heading1");
            WriteIssues(document, detail.Issues);
        }
        if (detail.Issues.Select(issue => issue.Deadline).Distinct(StringComparer.Ordinal).Skip(1).Any())
            document.Paragraph("各项问题请按表列期限分别完成；全部整改最迟于 " + deadline + " 完成。", "Meta");
        document.Paragraph(string.IsNullOrWhiteSpace(settings.IssuingUnit) ? settings.StreetName : settings.IssuingUnit, align: "right");
        document.Paragraph(inspection.Date, align: "right");
        document.Paragraph("整改情况回执", "Heading1");
        document.Table(null,
        [
            ["整改情况", "\n\n\n"],
            ["负责人签字", "　　　　　　　　　日期：　　　年　　月　　日"],
            ["责任单位盖章", "\n\n"]
        ], [21, 79], labelColumn: true);
        WriteSitePhotos(document, detail, photos, true);
    }

    public static void WriteLedger(Stream destination, WordLedger ledger, SettingsDto settings)
    {
        using var document = new Document(destination, true, "整改台账");
        document.Paragraph("生态环保问题整改台账", "Title");
        if (settings.StreetName.Length > 0) document.Paragraph(settings.StreetName, "Meta", "center");
        document.Paragraph($"排查日期：{ledger.From} 至 {ledger.To}　　社区：{ledger.CommunityName}", "Meta");
        string[] headers = ["序号", "排查日期", "点位", "社区", "问题描述", "整改期限", "状态", "反馈情况", "复查结论"];
        var rows = ledger.Issues.Select((issue, index) => new[]
        {
            (index + 1).ToString(CultureInfo.InvariantCulture), issue.InspectionDate, issue.PointName, issue.CommunityName,
            issue.Description, issue.Deadline, Status(issue.Status), WithDate(issue.FeedbackDate, issue.FeedbackNotes),
            WithDate(issue.ReviewDate, issue.ReviewConclusion)
        });
        document.Table(headers, rows, [4, 9, 12, 9, 23, 9, 9, 13, 12], fontSize: 21);
        if (ledger.Issues.Length == 0) document.Paragraph("所选条件下暂无问题记录。", "Meta");
        var closed = ledger.Issues.Count(issue => issue.Status == "closed");
        var overdue = ledger.Issues.Count(issue => issue.Status != "closed" && issue.Overdue);
        document.Paragraph($"共 {ledger.Issues.Length} 条，已闭环 {closed}，未闭环 {ledger.Issues.Length - closed}，逾期 {overdue}。", "Meta");

        if (!ledger.IncludePhotos) return;
        for (var index = 0; index < ledger.Issues.Length; index++)
        {
            var issue = ledger.Issues[index];
            document.Paragraph($"问题 {index + 1}　{issue.PointName}", "Heading1", pageBreak: true);
            document.Paragraph($"{issue.CommunityName}　排查：{issue.InspectionDate}　整改期限：{issue.Deadline}　{Status(issue.Status)}", "Meta");
            document.Paragraph(issue.Description);
            if (!string.IsNullOrWhiteSpace(issue.Requirement)) document.Paragraph("整改要求：" + issue.Requirement);
            var attached = ledger.Photos.Where(photo => photo.Photo.IssueId == issue.Id ||
                (photo.Photo.InspectionId == issue.InspectionId && photo.Photo.Phase == "site")).ToArray();
            if (attached.Length == 0)
            {
                document.Paragraph("本问题暂无照片。", "Meta");
                continue;
            }
            document.Paragraph("现场与整改后对比", "Heading1");
            document.Paragraph("照片按上传先后展示，包含历次反馈留痕；退回前的照片仍保留。", "Meta");
            document.Comparison(attached.Where(photo => photo.Photo.Phase == "site").ToArray(),
                attached.Where(photo => photo.Photo.Phase == "after").ToArray());
            var review = attached.Where(photo => photo.Photo.Phase == "review").ToArray();
            if (review.Length > 0)
            {
                document.Paragraph("复查照片", "Heading1");
                document.Gallery(review.Select((photo, number) => (photo, "复查 " + (number + 1))).ToArray());
            }
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
        // Keep the complete UTF-8 name below common 255-byte filesystem limits, including its extension.
        return $"{Safe(kind, 48)}_{Safe(pointName, 150)}_{Safe(isoDate.Replace("-", "", StringComparison.Ordinal), 24)}.docx";
    }

    private static string WithDate(string? date, string text) => string.IsNullOrWhiteSpace(date) ? text : date + "\n" + text;
    private static string Status(string status) => status switch { "pending" => "待整改", "feedback" => "社区已反馈", "closed" => "已复查闭环", _ => status };

    private static void WriteIssues(Document document, IssueDto[] issues) => document.Table(
        ["序号", "问题描述", "整改要求", "整改期限", "责任单位"],
        issues.Select((issue, index) => new[] { (index + 1).ToString(CultureInfo.InvariantCulture), issue.Description,
            string.IsNullOrWhiteSpace(issue.Requirement) ? "按上述问题完成整改。" : issue.Requirement, issue.Deadline, issue.ResponsibleUnit }),
        [6, 31, 27, 17, 19]);

    private static void WriteSitePhotos(Document document, InspectionDetailDto detail, WordPhoto[] photos, bool attachment)
    {
        var selected = photos.Where(photo => photo.Photo.Phase == "site").ToArray();
        if (attachment && selected.Length == 0) return;
        document.Paragraph(attachment ? "附件：现场问题照片" : "现场照片", "Heading1", pageBreak: attachment);
        if (selected.Length == 0)
        {
            document.Paragraph("本次排查未附现场照片。", "Meta");
            return;
        }
        var numbered = selected.Select((photo, index) =>
        {
            var issueIndex = Array.FindIndex(detail.Issues, issue => issue.Id == photo.Photo.IssueId);
            var caption = issueIndex < 0 ? "现场" : $"问题 {issueIndex + 1} · 现场";
            return (photo, caption + " " + (index + 1));
        }).ToArray();
        document.Gallery(numbered);
    }

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
        private readonly List<Asset> _assets = [];
        private int _drawingId;
        private int Width => _landscape ? 14570 : 9072;
        private sealed record Asset(string Id, string Relationship, string Target, string FilePath, JpegDimensions Dimensions);

        public Document(Stream destination, bool landscape, string title)
        {
            _landscape = landscape;
            _title = title;
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

        public void Paragraph(string text, string style = "Normal", string? align = null, bool indent = false, bool pageBreak = false)
        {
            Start("p");
            Start("pPr");
            Value("pStyle", style);
            if (pageBreak) Value("pageBreakBefore", "1");
            if (indent) { Start("ind"); Attr("firstLine", "640"); End(); }
            if (align is not null) Value("jc", align);
            End();
            TextRun(text);
            End();
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

        public void Table(string[]? headers, IEnumerable<string[]> rows, int[] weights, int fontSize = 21, bool labelColumn = false)
        {
            var widths = TableStart(weights);
            if (headers is not null) TableRow(headers, widths, fontSize, true, false);
            foreach (var row in rows) TableRow(row, widths, fontSize, false, labelColumn);
            End();
            // A table must not be the final cell child; this also provides a small, predictable gap in the body.
            Paragraph("", "TableGap");
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
                Start(edge); Attr("val", "single"); Attr("sz", "4"); Attr("color", "666666"); End();
            }
            End();
            Start("tblLayout"); Attr("type", "fixed"); End();
            Start("tblCellMar");
            foreach (var edge in new[] { "top", "left", "bottom", "right" }) { Start(edge); Attr("w", "100"); Attr("type", "dxa"); End(); }
            End();
            End();
            Start("tblGrid");
            foreach (var width in widths) { Start("gridCol"); Attr("w", width.ToString(CultureInfo.InvariantCulture)); End(); }
            End();
            return widths;
        }

        private void TableRow(string[] values, int[] widths, int fontSize, bool header, bool labelColumn)
        {
            Start("tr");
            if (header) { Start("trPr"); Value("tblHeader", "1"); End(); }
            for (var column = 0; column < widths.Length; column++)
            {
                CellStart(widths[column], header || (labelColumn && column == 0));
                Start("p"); Start("pPr"); Value("pStyle", "TableText"); End();
                TextRun(column < values.Length ? values[column] : "", fontSize, header || (labelColumn && column == 0));
                End(); End();
            }
            End();
        }

        private void CellStart(int width, bool shaded = false)
        {
            Start("tc"); Start("tcPr");
            Start("tcW"); Attr("w", width.ToString(CultureInfo.InvariantCulture)); Attr("type", "dxa"); End();
            if (shaded) { Start("shd"); Attr("val", "clear"); Attr("fill", "F2F2F2"); End(); }
            Value("vAlign", "center");
            End();
        }

        public void Gallery((WordPhoto Photo, string Caption)[] photos)
        {
            var widths = TableStart([1, 1]);
            for (var index = 0; index < photos.Length; index += 2)
            {
                Start("tr"); Start("trPr"); Value("cantSplit", "1"); End();
                for (var column = 0; column < 2; column++)
                {
                    CellStart(widths[column]);
                    if (index + column < photos.Length)
                    {
                        var item = photos[index + column];
                        PhotoCell(item.Photo, item.Caption, widths[column] - 220);
                    }
                    else Paragraph("", "Caption");
                    End();
                }
                End();
            }
            End(); Paragraph("", "TableGap");
        }

        public void Comparison(WordPhoto[] before, WordPhoto[] after)
        {
            var widths = TableStart([1, 1]);
            TableRow(["现场", "整改后（含历次反馈）"], widths, 21, true, false);
            var count = Math.Max(1, Math.Max(before.Length, after.Length));
            for (var index = 0; index < count; index++)
            {
                Start("tr"); Start("trPr"); Value("cantSplit", "1"); End();
                for (var column = 0; column < 2; column++)
                {
                    CellStart(widths[column]);
                    var photos = column == 0 ? before : after;
                    if (index < photos.Length) PhotoCell(photos[index], (column == 0 ? "现场 " : "整改后 ") + (index + 1), widths[column] - 220);
                    else Paragraph(index == 0 ? "暂无照片" : "", "Caption", "center");
                    End();
                }
                End();
            }
            End(); Paragraph("", "TableGap");
        }

        private void PhotoCell(WordPhoto photo, string caption, int maximumWidth)
        {
            var date = photo.Photo.UploadedAt.Length >= 10 ? photo.Photo.UploadedAt[..10] : photo.Photo.UploadedAt;
            Paragraph(caption + (date.Length == 0 ? "" : "　上传 " + date), "Caption", "center");
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
            Start("p"); Start("pPr"); Value("pStyle", "Photo"); Value("jc", "center"); End();
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
                    <w:settings xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:defaultTabStop w:val="720"/><w:updateFields w:val="true"/></w:settings>
                    """);
                WritePart("word/fontTable.xml", Fonts);
                WritePart("word/footer1.xml", """
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <w:ftr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:p><w:pPr><w:pStyle w:val="Footer"/><w:jc w:val="center"/></w:pPr><w:r><w:t xml:space="preserve">— </w:t></w:r><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r><w:r><w:t xml:space="preserve"> —</w:t></w:r></w:p></w:ftr>
                    """);
                using (var core = XmlWriter.Create(_zip.CreateEntry("docProps/core.xml").Open(), XmlSettings))
                {
                    core.WriteStartDocument();
                    core.WriteStartElement("cp", "coreProperties", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties");
                    core.WriteElementString("dc", "title", "http://purl.org/dc/elements/1.1/", _title);
                    core.WriteElementString("dc", "creator", "http://purl.org/dc/elements/1.1/", "街道生态环保留痕工具");
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
            <w:fonts xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:font w:name="方正小标宋简体"><w:altName w:val="宋体"/><w:family w:val="roman"/><w:pitch w:val="variable"/></w:font><w:font w:name="仿宋"><w:altName w:val="宋体"/><w:family w:val="roman"/><w:pitch w:val="variable"/></w:font><w:font w:name="宋体"><w:family w:val="roman"/><w:pitch w:val="variable"/></w:font></w:fonts>
            """;

        private const string Styles = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="仿宋" w:hAnsi="仿宋" w:eastAsia="仿宋" w:cs="仿宋"/><w:color w:val="000000"/><w:sz w:val="32"/><w:szCs w:val="32"/><w:lang w:val="zh-CN" w:eastAsia="zh-CN"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="360" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
              <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/><w:pPr><w:widowControl/></w:pPr></w:style>
              <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="0" w:after="360"/><w:jc w:val="center"/></w:pPr><w:rPr><w:rFonts w:ascii="方正小标宋简体" w:hAnsi="方正小标宋简体" w:eastAsia="方正小标宋简体" w:cs="方正小标宋简体"/><w:sz w:val="44"/><w:szCs w:val="44"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="200" w:after="120"/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:sz w:val="32"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="Meta"><w:name w:val="说明"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="140" w:line="280" w:lineRule="auto"/></w:pPr><w:rPr><w:rFonts w:ascii="宋体" w:hAnsi="宋体" w:eastAsia="宋体"/><w:sz w:val="21"/><w:szCs w:val="21"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="TableText"><w:name w:val="表格正文"/><w:basedOn w:val="Normal"/><w:pPr><w:widowControl w:val="0"/><w:spacing w:before="0" w:after="0" w:line="260" w:lineRule="auto"/></w:pPr><w:rPr><w:rFonts w:ascii="宋体" w:hAnsi="宋体" w:eastAsia="宋体"/><w:sz w:val="21"/><w:szCs w:val="21"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="TableGap"><w:name w:val="表格间距"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="0" w:after="100" w:line="20" w:lineRule="exact"/></w:pPr><w:rPr><w:sz w:val="2"/><w:szCs w:val="2"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="Caption"><w:name w:val="照片说明"/><w:basedOn w:val="Normal"/><w:pPr><w:keepNext/><w:spacing w:before="0" w:after="80" w:line="240" w:lineRule="auto"/></w:pPr><w:rPr><w:rFonts w:ascii="宋体" w:hAnsi="宋体" w:eastAsia="宋体"/><w:sz w:val="20"/><w:szCs w:val="20"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="Photo"><w:name w:val="照片"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/></w:pPr><w:rPr><w:sz w:val="2"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="Footer"><w:name w:val="footer"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/></w:pPr><w:rPr><w:rFonts w:ascii="宋体" w:hAnsi="宋体" w:eastAsia="宋体"/><w:sz w:val="18"/><w:szCs w:val="18"/></w:rPr></w:style>
            </w:styles>
            """;
    }
}
