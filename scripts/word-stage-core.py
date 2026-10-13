from pathlib import Path

def edit(path, old, new, count=1):
    p = Path(path)
    s = p.read_text()
    assert s.count(old) == count, (path, old[:100], s.count(old))
    p.write_text(s.replace(old, new))

p = 'server/WordExporter.cs'
edit(p, 'document.Paragraph("一、发现问题", "Heading1");', 'document.Section("发现问题");')
edit(p, 'document.Paragraph("三、备注", "Heading1");', 'document.Section("备注");')
edit(p, 'document.Paragraph("一、整治内容", "Heading1");', 'document.Section("整治内容");')
edit(p, 'document.Paragraph("二、整治成效", "Heading1");', 'document.Section("整治成效");')
edit(p, '''        var selected = SortPhotos(photos.Where(photo => photo.Photo.CampaignId == campaign.Id));
        if (selected.Length > 0)
        {
            document.Paragraph("三、整治照片", "Heading1");
            WriteCampaignPhotoTables(document, selected);
        }
        if (!string.IsNullOrWhiteSpace(campaign.Notes))
        {
            document.Paragraph("备注", "Heading1");
            document.Paragraph(campaign.Notes);
        }''', '''        // Keep short notes with the written record, not on a tail page after photographs.
        if (!string.IsNullOrWhiteSpace(campaign.Notes))
        {
            document.Section("备注");
            document.Paragraph(campaign.Notes);
        }
        var selected = SortPhotos(photos.Where(photo => photo.Photo.CampaignId == campaign.Id));
        if (selected.Length > 0)
        {
            document.Section("整治照片");
            WriteCampaignPhotoTables(document, selected, campaign.Name + " · " + campaign.Date);
        }''')
edit(p, '''            document.Table(null, values, [20, 80], labelColumn: true);
            WriteIssuePhotoTables(document, IssuePhotos(photos, issue));''', '''            var context = PhotoContext(issue, index + 1);
            document.Table(null, values, [20, 80], labelColumn: true, context: context);
            WriteIssuePhotoTables(document, IssuePhotos(photos, issue), context, actions);''')
edit(p, '''actions.OrderBy(action => action.Date, StringComparer.Ordinal)
                    .Select''', '''actions.Select''')
edit(p, '            WriteIssuePhotoTables(document, item.Photos);', '''            var actions = ledger.History?.FirstOrDefault(history => history.IssueId == item.Issue.Id)?.Actions ?? [];
            WriteIssuePhotoTables(document, item.Photos, PhotoContext(item.Issue, item.Index + 1), actions);''')
edit(p, '            WriteCampaignPhotoTables(document, item.Photos);', '            WriteCampaignPhotoTables(document, item.Photos, item.Campaign.Name + " · " + item.Campaign.Date);')
edit(p, '[5, 9, 13, 9, 22, 9, 8, 13, 12], centeredColumns: [0, 1, 5, 6],', '[5, 9, 13, 9, 22, 9, 8, 13, 12], centeredColumns: [0, 1, 5, 6], repeatColumns: [1, 2, 3],')
edit(p, 'rows, [5, 10, 18, 11, 10, 8, 9, 7, 12, 10], centeredColumns: [0, 1, 6, 7, 8],', 'rows, [5, 10, 18, 11, 10, 8, 9, 7, 12, 10], centeredColumns: [0, 1, 6, 7, 8], repeatColumns: [1, 2, 3],')
edit(p, 'rows, [4, 9, 14, 8, 8, 11, 6, 6, 7, 7, 20], centeredColumns: [0, 1], rightAlignedColumns: [6, 7, 8, 9],', 'rows, [4, 9, 14, 8, 8, 11, 6, 6, 7, 7, 20], centeredColumns: [0, 1], rightAlignedColumns: [6, 7, 8, 9], repeatColumns: [1, 2, 4],')
s = Path(p).read_text()
start = s.index('    private static void WriteIssuePhotoTables(')
end = s.index('    private static void WriteSitePhotos(', start)
edit(p, s[start:end], '''    private static string PhotoContext(IssueDto issue, int number) =>
        issue.PointName + " · " + issue.InspectionDate + " · 问题 " + Number(number);

    private static void WriteIssuePhotoTables(Document document, WordPhoto[] photos, string context, IssueActionDto[] actions)
    {
        var site = photos.Where(photo => photo.Photo.Phase == "site").ToArray();
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var feedbackRound = 0;
        var reviewRound = 0;
        var siteShown = false;
        // Rounds follow recorded action order, not upload time or image position.
        foreach (var action in actions)
        {
            if (action.Kind is not ("feedback" or "review")) continue;
            var feedback = action.Kind == "feedback";
            var round = feedback ? ++feedbackRound : ++reviewRound;
            var phase = feedback ? "after" : "review";
            var selected = photos.Where(photo => photo.Photo.ActionId == action.Id && photo.Photo.Phase == phase).ToArray();
            foreach (var photo in selected) assigned.Add(photo.Photo.Id);
            var heading = (feedback ? "整改后 · 第 " : "复查 · 第 ") + Number(round) +
                (feedback ? " 次反馈 · " : " 次复查 · ") + action.Date;
            if (feedback && !siteShown)
            {
                document.Comparison(site, selected, "整改前", heading, context);
                siteShown = true;
            }
            else document.Gallery(selected.Select(photo => (photo, "")).ToArray(), context + "\\n" + heading);
        }
        if (!siteShown) document.Gallery(site.Select(photo => (photo, "")).ToArray(), context + "\\n整改前 · 现场");
        foreach (var phase in new[] { "after", "review" })
        {
            var unassigned = photos.Where(photo => photo.Photo.Phase == phase && !assigned.Contains(photo.Photo.Id)).ToArray();
            document.Gallery(unassigned.Select(photo => (photo, "")).ToArray(), context + "\\n" +
                (phase == "after" ? "整改后" : "复查") + "（未关联办理轮次）");
        }
    }
    private static void WriteCampaignPhotoTables(Document document, WordPhoto[] photos, string context)
    {
        document.Comparison(photos.Where(photo => photo.Photo.Phase == "before").ToArray(), photos.Where(photo => photo.Photo.Phase == "after").ToArray(), "整治前", "整治后", context);
        document.Gallery(photos.Where(photo => photo.Photo.Phase == "process").Select(photo => (photo, "")).ToArray(), context + "\\n整治中");
    }
''')
edit(p, '        document.Paragraph(attachment ? "附件：现场问题照片" : "二、现场照片", "Heading1", pageBreak: attachment);', '''        if (attachment) document.Paragraph("附件：现场问题照片", "Heading1", pageBreak: true);
        else document.Section("现场照片");''')
edit(p, '''        }).ToArray());
    }

    private sealed record Cell''', '''        }).ToArray(), detail.Inspection.PointName + " · " + detail.Inspection.Date + " · 现场照片");
    }

    private sealed record Cell''')
edit(p, '        private int _drawingId;', '''        private int _drawingId;
        private int _sectionNumber;
        public void Section(string title) => Paragraph(ChineseNumber(++_sectionNumber) + "、" + title, "Heading1");''')
edit(p, 'bool labelColumn = false, bool keepTogether = false, int[]? centeredColumns = null, int[]? rightAlignedColumns = null, string? summary = null)', '''bool labelColumn = false, bool keepTogether = false, int[]? centeredColumns = null, int[]? rightAlignedColumns = null, string? summary = null,
            int[]? repeatColumns = null, string? context = null)''')
edit(p, '            if (headers is not null) CellRow', '''            if (context is not null) CellRow([new(context, Span: widths.Length, Header: true)], widths, header: true);
            if (headers is not null) CellRow''')
edit(p, '                CellRow(cells, widths, keepWithNext: keepWithNext, fontSize: fontSize);', '                CellRow(cells, widths, keepWithNext: keepWithNext, fontSize: fontSize, repeatColumns: repeatColumns);')
edit(p, 'private void CellRow(Cell[] cells, int[] widths, bool header = false, bool keepWithNext = false, int fontSize = 21)', 'private void CellRow(Cell[] cells, int[] widths, bool header = false, bool keepWithNext = false, int fontSize = 21, int[]? repeatColumns = null)')
edit(p, '''                                fragments[index].Length == 1 && (cell.Header || index == 0 &&
                                    int.TryParse(cell.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _)) ? cell.Text : ""''', '''                                cell.Header || index == 0 && int.TryParse(cell.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                                    ? cell.Text : repeatColumns?.Contains(index) == true
                                        ? fragments[index][0] + (index == 2 ? "（续）" : "") : ""''')
edit(p, '            var maximumLines = _landscape ? 18 : 28;', '''            // Bounded continuation blocks, not a page-height layout engine.
            // Keep font metrics, margins, logical row counts and cantSplit unchanged.
            var maximumLines = _landscape ? 6 : 12;''')
edit(p, 'public void Comparison(WordPhoto[] before, WordPhoto[] after, string beforeHeading, string afterHeading)', 'public void Comparison(WordPhoto[] before, WordPhoto[] after, string beforeHeading, string afterHeading, string? context = null)')
edit(p, '            CellRow([new(beforeHeading, Header: true), new(afterHeading, Header: true)], widths, header: true);', '''            if (context is not null) CellRow([new(context, Span: 2, Header: true)], widths, header: true);
            CellRow([new(beforeHeading, Header: true), new(afterHeading, Header: true)], widths, header: true);''')
edit(p, 'document.Paragraph(unit, align: "right");', 'document.Paragraph(unit, align: "right", keepNext: !string.IsNullOrWhiteSpace(date));')
edit(p, 'bool pageBreak = false)\n        {', 'bool pageBreak = false, bool keepNext = false)\n        {')
edit(p, '            if (pageBreak) Value("pageBreakBefore", "1");', '            if (pageBreak) Value("pageBreakBefore", "1");\n            if (keepNext) Value("keepNext", "1");')
tests = Path('scripts/word-regression-stage.txt').read_text()
p = Path('server/StreetEcoTool.Tests/WordExporterTests.cs')
s = p.read_text()
needle = '    private static InspectionDetailDto ClosedInspection()'
assert s.count(needle) == 1
p.write_text(s.replace(needle, tests + '\n' + needle))
Path('/tmp/hbtool-stage-message.txt').write_text('fix(docx): W01-W05 sections, continuation context and action-grouped photos\n')
