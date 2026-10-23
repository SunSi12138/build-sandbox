using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace StreetEcoTool;

public sealed record ImportRow(int Row, string SourceKey, string Kind, string Name, string Grid, string Type, string Address, string Contact, string Phone, string Notes, GeoShape? Shape, string Error = "", string Warning = "", bool Duplicate = false);
public sealed record ImportPreview(ImportRow[] Rows, string FileName);
public sealed record ImportApply(ImportRow[] Rows);
public sealed record ImportResult(int Added, int Skipped);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImportPreview))]
[JsonSerializable(typeof(ImportApply))]
[JsonSerializable(typeof(ImportResult))]
[JsonSerializable(typeof(double[][]))]
public partial class ImportJsonContext : JsonSerializerContext;

public sealed partial class Store
{
    public ImportPreview PreviewImport(byte[] bytes, string name, string kind, string coordinates)
    {
        if (kind is not ("grid" or "point")) throw new ApiException(400, "请选择导入网格或场所。");
        if (coordinates is not ("wgs84" or "gcj02")) throw new ApiException(400, "请选择坐标来源。");
        string[][] cells;
        try { cells = Path.GetExtension(name).ToLowerInvariant() switch { ".xlsx" => ImportReader.Xlsx(bytes), ".csv" => ImportReader.Csv(bytes), _ => throw new ApiException(400, "请选择 .xlsx 或 UTF-8 格式的 .csv 文件。") }; }
        catch (Exception e) when (e is InvalidDataException or XmlException or FormatException) { throw new ApiException(400, "表格无法读取，请另存为标准 .xlsx 文件后重试。"); }
        var headerIndex = Array.FindIndex(cells, row => row.Any(x => x.Trim() is "名称" or "Name" or "场所名称" or "网格名称"));
        if (headerIndex < 0) throw new ApiException(400, "没有找到名称列，请使用导入模板或原地图的 GridData.xlsx。");
        var headers = cells[headerIndex].Select(x => x.Trim()).ToArray();
        string Cell(string[] row, params string[] aliases) { var index = Array.FindIndex(headers, h => aliases.Contains(h, StringComparer.OrdinalIgnoreCase)); return index >= 0 && index < row.Length ? row[index].Trim() : ""; }
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var result = new List<ImportRow>();
        lock (_gate)
        {
            for (var i = headerIndex + 1; i < cells.Length; i++)
            {
                var row = cells[i];
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                var title = Cell(row, "名称", "Name", "场所名称", "网格名称");
                var grid = Cell(row, "网格", "所属网格", "社区");
                var type = Cell(row, "场所形态", "场所类型", "类型");
                if (type.Length == 0) type = "其他";
                var warning = ""; var error = ""; GeoShape? shape = null;
                try
                {
                    var raw = Cell(row, "Coordinates", "区域坐标");
                    if (raw.Length > 0)
                    {
                        var points = JsonSerializer.Deserialize(raw, ImportJsonContext.Default.DoubleArrayArray) ?? [];
                        if (points.Length > 1 && !points[0].SequenceEqual(points[^1])) points = [.. points, points[0]];
                        shape = new(points.Length == 1 ? "Point" : "Polygon", points);
                        warning = "导入前请核对地图位置";
                    }
                    else
                    {
                        var lon = Cell(row, "经度", "Longitude"); var lat = Cell(row, "纬度", "Latitude");
                        if (lon.Length > 0 || lat.Length > 0) shape = new("Point", [[double.Parse(lon, CultureInfo.InvariantCulture), double.Parse(lat, CultureInfo.InvariantCulture)]]);
                    }
                    if (shape is not null)
                    {
                        ValidateShape(shape);
                        if (coordinates == "gcj02") shape = shape with { Coordinates = shape.Coordinates.Select(CoordinateConversion.ToWgs84).ToArray() };
                    }
                    if (title.Length == 0) error = "请填写名称";
                    else if (title.Contains('\uFFFD')) error = "原文件名称乱码，请修改名称";
                    else if (kind == "point" && grid.Length == 0) error = "请填写所属网格";
                    else if (kind == "point" && Cell(row, "地址", "详细地址") == "") error = "请填写详细地址";
                }
                catch (Exception e) when (e is JsonException or FormatException or OverflowException or ApiException) { error = "坐标有误，请修改原表或取消该行"; }
                var item = new ImportRow(i + 1, hash + ":" + (i + 1), kind, title, grid, type, Cell(row, "地址", "详细地址"), Cell(row, "联系人", "Contact"), Cell(row, "电话", "联系电话", "Phone"), Cell(row, "备注", "Notes"), shape, error, warning);
                result.Add(item with { Duplicate = ImportDuplicate(item) });
            }
        }
        if (result.Count == 0) throw new ApiException(400, "表格没有可导入的资料。");
        return new([.. result], Path.GetFileName(name));
    }

    private bool ImportDuplicate(ImportRow row, Microsoft.Data.Sqlite.SqliteTransaction? transaction = null)
    {
        using var command = Command("""
            SELECT EXISTS(SELECT 1 FROM imported_rows WHERE source_key=$key)
             OR ($kind='grid' AND EXISTS(SELECT 1 FROM communities WHERE name=$name))
             OR ($kind='point' AND EXISTS(SELECT 1 FROM points p JOIN communities c ON c.id=p.community_id WHERE p.name=$name AND c.name=$grid AND p.address=$address));
            """, transaction, ("$key", row.SourceKey), ("$kind", row.Kind), ("$name", row.Name.Trim()), ("$grid", row.Grid.Trim()), ("$address", row.Address.Trim()));
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    public ImportResult ApplyImport(ImportApply request)
    {
        if (request.Rows is null || request.Rows.Length is < 1 or > 5000 || request.Rows.Any(x => x is null)) throw new ApiException(400, "请勾选1至5000条资料。");
        lock (_gate)
        {
            PrepareWrite();
            var communities = GetCommunities();
            var pointTypes = GetPointTypes();
            using var transaction = _db.BeginTransaction();
            var added = 0; var skipped = 0;
            foreach (var row in request.Rows)
            {
                var name = Text(row.Name, $"第{row.Row}行名称", 200, true);
                if (name.Contains('\uFFFD')) throw new ApiException(400, $"第{row.Row}行名称有乱码，请修改后导入。");
                if (row.Kind is not ("grid" or "point")) throw new ApiException(400, "资料类型不正确。");
                if (row.SourceKey.Length is < 1 or > 100) throw new ApiException(400, "导入编号不正确，请重新选择文件。");
                if (row.Shape is not null) ValidateShape(row.Shape);
                if (ImportDuplicate(row, transaction)) { skipped++; continue; }
                string id;
                if (row.Kind == "grid")
                {
                    id = NewId();
                    using var add = Command("INSERT INTO communities(id,name,title) VALUES($id,$name,$name);", transaction, ("$id", id), ("$name", name)); add.ExecuteNonQuery();
                    if (row.Shape is not null)
                    {
                        using var shape = Command("INSERT INTO map_shapes(kind,record_id,shape,revision) VALUES('grid',$id,$shape,1);", transaction, ("$id", id), ("$shape", JsonSerializer.Serialize(row.Shape, CommunityJsonContext.Default.GeoShape))); shape.ExecuteNonQuery();
                    }
                    AddCommunityHistory("grid", id, "", JsonSerializer.Serialize(new CommunityWrite("", name, JsonSerializer.SerializeToElement(row.Shape, CommunityJsonContext.Default.GeoShape)), CommunityJsonContext.Default.CommunityWrite), "导入网格", transaction);
                }
                else
                {
                    var gridName = Text(row.Grid, "所属网格", 200, true);
                    var grid = communities.FirstOrDefault(x => x.Name == gridName) ?? throw new ApiException(400, $"第{row.Row}行的网格“{gridName}”不存在，请先添加或导入网格。");
                    var type = pointTypes.FirstOrDefault(x => x.Name == row.Type.Trim()) ?? throw new ApiException(400, $"第{row.Row}行的场所类型“{row.Type}”不存在，请在设置中添加。");
                    id = SavePointCore(null, new(name, grid.Id, type.Id, row.Address, row.Contact, row.Phone, Notes: row.Notes, Shape: row.Shape), transaction);
                }
                using var imported = Command("INSERT INTO imported_rows(source_key,record_id,kind) VALUES($key,$id,$kind);", transaction, ("$key", row.SourceKey), ("$id", id), ("$kind", row.Kind)); imported.ExecuteNonQuery();
                added++;
            }
            transaction.Commit();
            return new(added, skipped);
        }
    }
}

internal static class ImportReader
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    internal static string[][] Xlsx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > 2000 || zip.Entries.Sum(x => x.Length) > 80L * 1024 * 1024) throw new ApiException(400, "表格过大，请分批导入。");
        XDocument Read(ZipArchiveEntry entry) { using var input = entry.Open(); using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 40_000_000 }); return XDocument.Load(reader); }
        var shared = zip.GetEntry("xl/sharedStrings.xml") is { } strings ? Read(strings).Descendants(S + "si").Select(x => string.Concat(x.Descendants(S + "t").Select(t => t.Value))).ToArray() : [];
        var workbook = Read(zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException());
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var sheetId = workbook.Descendants(S + "sheet").FirstOrDefault()?.Attribute(r + "id")?.Value;
        var relationships = Read(zip.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidDataException());
        var target = relationships.Root?.Elements().FirstOrDefault(x => x.Attribute("Id")?.Value == sheetId)?.Attribute("Target")?.Value ?? throw new InvalidDataException();
        var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        var sheet = Read(zip.GetEntry(path) ?? throw new InvalidDataException());
        var result = new List<string[]>();
        foreach (var row in sheet.Descendants(S + "row"))
        {
            if (result.Count > 5003) throw new ApiException(400, "一次最多导入5000行，请拆分表格。");
            var values = new string[64]; Array.Fill(values, ""); var next = 0;
            foreach (var cell in row.Elements(S + "c"))
            {
                var address = cell.Attribute("r")?.Value; var index = 0;
                if (address is not null) { foreach (var c in address.TakeWhile(char.IsLetter)) index = checked(index * 26 + char.ToUpperInvariant(c) - 'A' + 1); index--; } else index = next;
                next = index + 1; if (index is < 0 or >= 64) continue;
                var value = cell.Element(S + "v")?.Value ?? "";
                var type = cell.Attribute("t")?.Value;
                if (type == "s") { if (!int.TryParse(value, out var n) || n < 0 || n >= shared.Length) throw new InvalidDataException(); value = shared[n]; }
                else if (type == "inlineStr") value = string.Concat(cell.Descendants(S + "t").Select(x => x.Value));
                values[index] = value;
            }
            result.Add(values);
        }
        return [.. result];
    }
    internal static string[][] Csv(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new ApiException(400, "CSV 请另存为 UTF-8 编码，或直接使用 Excel 文件。"); }
        var rows = new List<string[]>(); var row = new List<string>(); var field = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else quoted = !quoted; }
            else if (!quoted && c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (!quoted && (c == '\r' || c == '\n')) { row.Add(field.ToString()); field.Clear(); rows.Add([.. row]); row.Clear(); if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else field.Append(c);
            if (rows.Count > 5003 || row.Count > 64) throw new ApiException(400, "表格过大，请分批导入。");
        }
        if (quoted) throw new ApiException(400, "CSV 引号未闭合，请重新另存表格。");
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add([.. row]); }
        return [.. rows];
    }
}

public static class ImportApi
{
    public static void Map(WebApplication app, Store store)
    {
        RequestDelegate preview = async c =>
        {
            var form = await c.Request.ReadFormAsync(c.RequestAborted);
            var file = form.Files.GetFile("file") ?? throw new ApiException(400, "请选择文件。");
            if (file.Length is < 1 or > 20 * 1024 * 1024) throw new ApiException(400, "文件不能超过20MB。");
            using var stream = new MemoryStream(); await file.CopyToAsync(stream, c.RequestAborted);
            await Api.Write(c, store.PreviewImport(stream.ToArray(), file.FileName, form["kind"].ToString(), form["coordinates"].ToString()), ImportJsonContext.Default.ImportPreview);
        };
        RequestDelegate apply = async c => await Api.Write(c, store.ApplyImport(await Api.Read(c, ImportJsonContext.Default.ImportApply)), ImportJsonContext.Default.ImportResult);
        RequestDelegate template = async c =>
        {
            var grid = c.Request.Query["kind"] == "grid";
            var report = new ReportDto(grid ? "网格导入模板" : "场所导入模板", "从第4行填写；不要修改列名", grid ? ["名称", "区域坐标"] : ["名称", "网格", "场所形态", "地址", "联系人", "电话", "经度", "纬度", "备注"], []);
            using var stream = new MemoryStream(); ExcelReport.Write(stream, report); stream.Position = 0;
            c.Response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            c.Response.Headers.ContentDisposition = "attachment; filename=import-template.xlsx";
            await stream.CopyToAsync(c.Response.Body, c.RequestAborted);
        };
        app.MapPost("/api/import/preview", preview); app.MapPost("/api/import/apply", apply); app.MapGet("/api/import/template", template);
    }
}
