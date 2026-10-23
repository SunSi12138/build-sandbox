using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreetEcoTool;

public sealed record GeoShape(string Type, double[][] Coordinates);
public sealed record ShapeWrite(GeoShape Shape, int Revision = 0);
public sealed record MapFeatureDto(string Id, string Kind, string Name, string CommunityId, GeoShape Shape, int Revision, int OpenIssues = 0, bool Disabled = false);
public sealed record MapLayerDto(string Id, string Name, string Folder, string Scheme, bool Enabled, bool IsDefault, double Opacity, int MinZoom, int MaxZoom, int SortOrder);
public sealed record MapStateDto(MapLayerDto[] Layers, MapFeatureDto[] Features);
public sealed record CommunityRecord(string Id, string Kind, string ParentId, string Name, JsonElement Data, int Revision, string UpdatedAt, bool Archived);
public sealed record CommunityWrite(string ParentId, string Name, JsonElement Data, int Revision = 0, bool Archived = false);
public sealed record HistoryDto(string Id, string Kind, string RecordId, string At, string Action, string Before, string After);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MapStateDto))]
[JsonSerializable(typeof(MapLayerDto))]
[JsonSerializable(typeof(MapLayerDto[]))]
[JsonSerializable(typeof(MapFeatureDto))]
[JsonSerializable(typeof(ShapeWrite))]
[JsonSerializable(typeof(GeoShape))]
[JsonSerializable(typeof(CommunityRecord))]
[JsonSerializable(typeof(CommunityRecord[]))]
[JsonSerializable(typeof(CommunityWrite))]
[JsonSerializable(typeof(HistoryDto[]))]
public partial class CommunityJsonContext : JsonSerializerContext;
