using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreetEcoTool;

public sealed record GeoShape(string Type, double[][] Coordinates, double[][][]? Areas = null);
public sealed record ShapeWrite(GeoShape Shape, int Revision = 0);
public sealed record MapUseLabel(string Id, string Name, string PurposeName, string Icon);
public sealed record MapFeatureDto(string Id, string Kind, string Name, string CommunityId, GeoShape Shape, int Revision, int OpenIssues = 0, bool Disabled = false, string TypeId = "", string Color = "#087f70", string[]? PurposeIds = null, MapUseLabel[]? Uses = null);
public sealed record MapLayerDto(string Id, string Name, string Folder, string Scheme, bool Enabled, bool IsDefault, double Opacity, int MinZoom, int MaxZoom, int SortOrder, string UrlTemplate = "", string ApiKey = "", string Subdomains = "0,1,2,3", bool UpdateCache = false, int CacheVersion = 0, bool Annotation = false, string MapKey = "");
public sealed record PurposeField(string Id, string Label, string Type, bool Required);
public sealed record PlaceDossierWrite(PointWriteRequest Point, CommunityWrite[] Uses);
public sealed record MapStateDto(MapLayerDto[] Layers, MapFeatureDto[] Features);
public sealed record CommunityRecord(string Id, string Kind, string ParentId, string Name, JsonElement Data, int Revision, string UpdatedAt, bool Archived);
public sealed record CommunityWrite(string ParentId, string Name, JsonElement Data, int Revision = 0, bool Archived = false);
public sealed record HistoryDto(string Id, string Kind, string RecordId, string At, string Action, string Before, string After);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MapStateDto))]
[JsonSerializable(typeof(PlaceDossierWrite))]
[JsonSerializable(typeof(PurposeField[]))]
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
