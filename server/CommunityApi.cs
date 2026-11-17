namespace StreetEcoTool;

public static class CommunityApi
{
    public static void Map(WebApplication app, Store store)
    {
        ImportApi.Map(app, store);
        store.MapManagementApi(app);
        store.MapArchiveApi(app);
        store.MapCacheImportApi(app);
        store.MapTileUpdateApi(app);
        RequestDelegate dossier = async c => await Api.Write(c, store.CreatePlaceDossier(await Api.Read(c, CommunityJsonContext.Default.PlaceDossierWrite)), AppJsonContext.Default.PointDto, 201);
        app.MapPost("/api/place-dossiers", dossier);
        store.MapStatisticsApi(app);
        RequestDelegate map = context => Api.Write(context, store.GetMapState(), CommunityJsonContext.Default.MapStateDto);
        app.MapGet("/api/map", map);
        RequestDelegate coordinates = context =>
        {
            if (!double.TryParse(context.Request.Query["lon"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon) || !double.TryParse(context.Request.Query["lat"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) || !double.IsFinite(lon) || !double.IsFinite(lat) || Math.Abs(lon)>180 || Math.Abs(lat)>85) throw new ApiException(400, "请填写正确的经度、纬度。");
            var system = context.Request.Query["system"].ToString();
            if (system is not ("wgs84" or "gcj02")) throw new ApiException(400, "请选择坐标来源。");
            return Api.Write(context, new GeoShape("Point", [system == "gcj02" ? CoordinateConversion.ToWgs84([lon, lat]) : [lon, lat]]), CommunityJsonContext.Default.GeoShape);
        };
        app.MapGet("/api/map/coordinates", coordinates);
        RequestDelegate shape = async context => await Api.Write(context, store.SaveShape((string)context.Request.RouteValues["kind"]!, Api.Id(context), await Api.Read(context, CommunityJsonContext.Default.ShapeWrite)), CommunityJsonContext.Default.MapFeatureDto);
        app.MapPut("/api/map/shapes/{kind}/{id}", shape);
        RequestDelegate layers = context => Api.Write(context, store.GetMapLayers(), CommunityJsonContext.Default.MapLayerDtoArray);
        RequestDelegate saveLayer = async context => await Api.Write(context, store.SaveMapLayer(await Api.Read(context, CommunityJsonContext.Default.MapLayerDto)), CommunityJsonContext.Default.MapLayerDto);
        app.MapGet("/api/map/layers", layers);
        app.MapPost("/api/map/layers", saveLayer);
        RequestDelegate tile = async context =>
        {
            if (!int.TryParse(context.Request.RouteValues["z"]?.ToString(), out var z) || !int.TryParse(context.Request.RouteValues["x"]?.ToString(), out var x) || !int.TryParse(context.Request.RouteValues["y"]?.ToString(), out var y)) throw new ApiException(400, "地图编号不正确。");
            var path = await store.ResolveTile((string)context.Request.RouteValues["layer"]!, z, x, y, context.RequestAborted);
            if (path is null) { context.Response.StatusCode = 404; return; }
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.ContentType = Path.GetExtension(path) switch { ".jpg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png" };
            await context.Response.SendFileAsync(path, context.RequestAborted);
        };
        app.MapGet("/api/map/tiles/{layer}/{z}/{x}/{y}", tile);
        RequestDelegate records = context => Api.Write(context, store.ListCommunityRecords((string)context.Request.RouteValues["kind"]!, context.Request.Query["parentId"].FirstOrDefault(), context.Request.Query["all"] == "true"), CommunityJsonContext.Default.CommunityRecordArray);
        RequestDelegate add = async context => await Api.Write(context, store.SaveCommunityRecord((string)context.Request.RouteValues["kind"]!, null, await Api.Read(context, CommunityJsonContext.Default.CommunityWrite)), CommunityJsonContext.Default.CommunityRecord, 201);
        RequestDelegate edit = async context => await Api.Write(context, store.SaveCommunityRecord((string)context.Request.RouteValues["kind"]!, Api.Id(context), await Api.Read(context, CommunityJsonContext.Default.CommunityWrite)), CommunityJsonContext.Default.CommunityRecord);
        RequestDelegate history = context => Api.Write(context, store.CommunityHistory(Api.Id(context)), CommunityJsonContext.Default.HistoryDtoArray);
        app.MapGet("/api/records/{kind}", records);
        RequestDelegate record = context => Api.Write(context, store.ListCommunityRecords((string)context.Request.RouteValues["kind"]!, includeArchived: true).FirstOrDefault(x => x.Id == Api.Id(context)) ?? throw new ApiException(404, "资料不存在。"), CommunityJsonContext.Default.CommunityRecord);
        app.MapGet("/api/records/{kind}/{id}", record);
        app.MapPost("/api/records/{kind}", add);
        app.MapPut("/api/records/{kind}/{id}", edit);
        app.MapGet("/api/history/{id}", history);
        RequestDelegate photos = context => Api.Write(context, store.GetRecordPhotos(Api.Id(context)), AppJsonContext.Default.PhotoDtoArray);
        app.MapGet("/api/record-photos/{id}", photos);
    }
}
