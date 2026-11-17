using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreetEcoTool;

public sealed partial class Store
{
    private static PurposeField[] ReadPurposeFields(JsonElement data) => data.TryGetProperty("fields", out var fields)
        ? fields.ValueKind == JsonValueKind.Array ? JsonSerializer.Deserialize(fields, CommunityJsonContext.Default.PurposeFieldArray) ?? [] : throw new ApiException(400, "用途字段格式不正确。") : [];

    private static void ValidatePurposeFields(JsonElement data)
    {
        var fields = ReadPurposeFields(data);
        if (fields.Length > 40 || fields.Select(f => f.Id).Distinct().Count() != fields.Length) throw new ApiException(400, "字段最多 40 项，字段编号不能重复。");
        foreach (var field in fields)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(field.Id, "^[a-zA-Z0-9_-]{1,60}$")) throw new ApiException(400, "字段编号不正确。");
            _ = Text(field.Label, "字段名称", 60, true);
            if (field.Type is not ("text" or "number")) throw new ApiException(400, "字段类型请选择文本或数字。");
        }
        if (fields.Select(f => f.Label.Trim()).Distinct().Count() != fields.Length) throw new ApiException(400, "字段名称不能重复。");
    }

    private JsonElement PrepareUseData(JsonElement data)
    {
        var purpose = ListCommunityRecords("purpose-type", includeArchived: true).FirstOrDefault(x => x.Id == JsonText(data, "purposeId")) ?? throw new ApiException(400, "请选择有效用途。");
        var fields = ReadPurposeFields(purpose.Data);
        var node = JsonNode.Parse(data.GetRawText())!.AsObject();
        var values = new JsonObject();
        var supplied = data.TryGetProperty("values", out var entries) && entries.ValueKind == JsonValueKind.Object;
        foreach (var field in fields)
        {
            JsonElement value = default;
            var present = supplied && entries.TryGetProperty(field.Id, out value);
            var valueText = present ? value.ToString().Trim() : "";
            if (field.Required && valueText.Length == 0) throw new ApiException(400, $"请填写{field.Label}。");
            if (valueText.Length == 0) continue;
            if (field.Type == "number")
            {
                if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) throw new ApiException(400, $"{field.Label}请填写数字。");
                values[field.Id] = number;
            }
            else values[field.Id] = Text(valueText, field.Label, 4000);
        }
        node["values"] = values;
        node["fieldSnapshot"] = JsonNode.Parse(JsonSerializer.Serialize(fields, CommunityJsonContext.Default.PurposeFieldArray));
        using var result = JsonDocument.Parse(node.ToJsonString()); return result.RootElement.Clone();
    }

    public static (string Label, string Value)[] UseFieldValues(JsonElement data)
    {
        if (!data.TryGetProperty("fieldSnapshot", out var fields) || fields.ValueKind != JsonValueKind.Array || !data.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object) return [];
        return fields.EnumerateArray().Select(field => (Label: JsonText(field,"label"), Value: values.TryGetProperty(JsonText(field,"id"),out var value) ? value.ToString() : "")).Where(x => x.Value.Length > 0).ToArray();
    }
}
