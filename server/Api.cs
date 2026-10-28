using System.Globalization;
using System.Text.Json.Serialization.Metadata;

namespace StreetEcoTool;

public static class Api
{
    public static async Task<T> Read<T>(HttpContext context, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (!context.Request.HasJsonContentType()) throw new ApiException(415, "请使用 application/json 提交数据。");
        var result = await context.Request.ReadFromJsonAsync(typeInfo, context.RequestAborted);
        return result ?? throw new ApiException(400, "提交内容不能为空。");
    }

    public static Task Write<T>(HttpContext context, T value, JsonTypeInfo<T> typeInfo, int statusCode = 200)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(value, typeInfo, contentType: null, cancellationToken: context.RequestAborted);
    }

    public static string Id(HttpContext context) => context.Request.RouteValues["id"] as string ?? throw new ApiException(400, "缺少记录 ID。");
    private static string? Query(HttpContext context, string name)
    {
        var value = context.Request.Query[name].ToString();
        return value.Length == 0 ? null : value;
    }
    private static int Number(HttpContext context, string name, int defaultValue)
    {
        var text = Query(context, name);
        if (text is null) return defaultValue;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw new ApiException(400, $"{name} 必须是非负整数。");
    }
    private static bool Flag(HttpContext context, string name)
    {
        var value = Query(context, name);
        return value switch { null or "false" or "0" => false, "true" or "1" => true, _ => throw new ApiException(400, $"{name} 必须是 true 或 false。") };
    }
    public static void RequireDeleteConfirmation(HttpContext context)
    {
        if (!string.Equals(Query(context, "confirm"), "true", StringComparison.Ordinal)) throw new ApiException(400, "请先确认删除，再提交 confirm=true。");
    }
    private static Task Deleted(HttpContext context) { context.Response.StatusCode = 204; return Task.CompletedTask; }

    public static void Map(WebApplication app, Store store)
    {
        RequestDelegate health = context => Write(context, new OkDto(), AppJsonContext.Default.OkDto);
        RequestDelegate bootstrap = context => Write(context, store.GetBootstrap(), AppJsonContext.Default.BootstrapDto);
        RequestDelegate getSettings = context => Write(context, store.GetSettings(), AppJsonContext.Default.SettingsDto);
        RequestDelegate saveSettings = async context => await Write(context, store.SaveSettings(await Read(context, AppJsonContext.Default.SettingsDto)), AppJsonContext.Default.SettingsDto);
        RequestDelegate dashboard = context => Write(context, store.GetDashboard(), AppJsonContext.Default.DashboardDto);
        app.MapGet("/api/health", health);
        app.MapGet("/api/bootstrap", bootstrap);
        app.MapGet("/api/settings", getSettings);
        app.MapPut("/api/settings", saveSettings);
        app.MapGet("/api/dashboard", dashboard);

        RequestDelegate communities = context => Write(context, store.GetCommunities(), AppJsonContext.Default.CommunityDtoArray);
        RequestDelegate createCommunity = async context => await Write(context, store.SaveCommunity(null, await Read(context, AppJsonContext.Default.CommunityWriteRequest)), AppJsonContext.Default.CommunityDto, 201);
        RequestDelegate updateCommunity = async context => await Write(context, store.SaveCommunity(Id(context), await Read(context, AppJsonContext.Default.CommunityWriteRequest)), AppJsonContext.Default.CommunityDto);
        RequestDelegate deleteCommunity = context => { RequireDeleteConfirmation(context); store.DeleteCommunity(Id(context)); return Deleted(context); };
        app.MapGet("/api/communities", communities);
        app.MapPost("/api/communities", createCommunity);
        app.MapPut("/api/communities/{id}", updateCommunity);
        app.MapDelete("/api/communities/{id}", deleteCommunity);

        RequestDelegate types = context => Write(context, store.GetPointTypes(), AppJsonContext.Default.PointTypeDtoArray);
        RequestDelegate createType = async context => await Write(context, store.SavePointType(null, await Read(context, AppJsonContext.Default.PointTypeWriteRequest)), AppJsonContext.Default.PointTypeDto, 201);
        RequestDelegate updateType = async context => await Write(context, store.SavePointType(Id(context), await Read(context, AppJsonContext.Default.PointTypeWriteRequest)), AppJsonContext.Default.PointTypeDto);
        RequestDelegate deleteType = context => { RequireDeleteConfirmation(context); store.DeletePointType(Id(context)); return Deleted(context); };
        app.MapGet("/api/point-types", types);
        app.MapPost("/api/point-types", createType);
        app.MapPut("/api/point-types/{id}", updateType);
        app.MapDelete("/api/point-types/{id}", deleteType);

        RequestDelegate points = context => Write(context, store.ListPoints(new(Query(context, "q"), Query(context, "communityId"), Query(context, "typeId"),
            Query(context, "disabled") ?? "false", Number(context, "page", 1), Number(context, "pageSize", 30), Flag(context, "recent"), Flag(context, "missed"), Query(context, "temporary") ?? "false", Flag(context, "map"))), AppJsonContext.Default.PointPage);
        RequestDelegate point = context => Write(context, store.GetPointDetail(Id(context)), AppJsonContext.Default.PointDetailDto);
        RequestDelegate createPoint = async context => await Write(context, store.SavePoint(null, await Read(context, AppJsonContext.Default.PointWriteRequest)), AppJsonContext.Default.PointDto, 201);
        RequestDelegate updatePoint = async context => await Write(context, store.SavePoint(Id(context), await Read(context, AppJsonContext.Default.PointWriteRequest)), AppJsonContext.Default.PointDto);
        RequestDelegate deletePoint = context => { RequireDeleteConfirmation(context); store.DeletePoint(Id(context)); return Deleted(context); };
        app.MapGet("/api/points", points);
        app.MapGet("/api/points/{id}", point);
        app.MapPost("/api/points", createPoint);
        app.MapPut("/api/points/{id}", updatePoint);
        RequestDelegate pointPhotos = async context => await Write(context,
            store.AttachPointPhotos(Id(context), await Read(context, AppJsonContext.Default.PointPhotosRequest)), AppJsonContext.Default.PointDto);
        RequestDelegate pointStatus = async context => await Write(context,
            store.SetPointDisabled(Id(context), await Read(context, AppJsonContext.Default.PointStatusRequest)), AppJsonContext.Default.PointDto);
        app.MapPut("/api/points/{id}/photos", pointPhotos);
        app.MapPut("/api/points/{id}/status", pointStatus);
        app.MapDelete("/api/points/{id}", deletePoint);
        RequestDelegate promotePoint = async context => await Write(context, store.PromotePoint(Id(context), await Read(context, AppJsonContext.Default.PointPromoteRequest)), AppJsonContext.Default.PointDto);
        app.MapPost("/api/points/{id}/promote", promotePoint);

        static InspectionQuery InspectionFilters(HttpContext context) => new(Query(context, "pointId"), Query(context, "communityId"),
            Query(context, "from"), Query(context, "to"), Query(context, "q"), Number(context, "page", 1), Number(context, "pageSize", 30),
            Query(context, "rectify"), Flag(context, "overdue"), Flag(context, "dueSoon"), Query(context, "typeId"), Flag(context, "temporary"),
            Flag(context, "awaitingReview"), Flag(context, "openOnly"), Flag(context, "map"));
        RequestDelegate inspections = context => Write(context, store.ListInspections(InspectionFilters(context)), AppJsonContext.Default.InspectionPage);
        RequestDelegate inspectionCounts = context => Write(context, store.GetInspectionCounts(InspectionFilters(context)), AppJsonContext.Default.InspectionCountsDto);
        RequestDelegate inspection = context => Write(context, store.GetInspectionDetail(Id(context)), AppJsonContext.Default.InspectionDetailDto);
        RequestDelegate createInspection = async context => await Write(context, store.CreateInspection(await Read(context, AppJsonContext.Default.InspectionCreateRequest)), AppJsonContext.Default.InspectionDetailDto, 201);
        RequestDelegate updateInspection = async context => await Write(context, store.UpdateInspection(Id(context), await Read(context, AppJsonContext.Default.InspectionUpdateRequest)), AppJsonContext.Default.InspectionDetailDto);
        RequestDelegate deleteInspection = context => { RequireDeleteConfirmation(context); store.DeleteInspection(Id(context)); return Deleted(context); };
        RequestDelegate batchFeedback = async context => await Write(context, store.RecordInspectionFeedback(Id(context), await Read(context, AppJsonContext.Default.InspectionFeedbackRequest)), AppJsonContext.Default.InspectionDetailDto);
        RequestDelegate batchReview = async context => await Write(context, store.ReviewInspection(Id(context), await Read(context, AppJsonContext.Default.InspectionReviewRequest)), AppJsonContext.Default.InspectionDetailDto);
        app.MapGet("/api/inspections", inspections);
        app.MapGet("/api/inspections/counts", inspectionCounts);
        app.MapGet("/api/inspections/{id}", inspection);
        app.MapPost("/api/inspections", createInspection);
        app.MapPut("/api/inspections/{id}", updateInspection);
        app.MapDelete("/api/inspections/{id}", deleteInspection);
        app.MapPost("/api/inspections/{id}/feedback", batchFeedback);
        app.MapPost("/api/inspections/{id}/review", batchReview);

        RequestDelegate campaignTypes = context => Write(context, store.GetCampaignTypes(), AppJsonContext.Default.CampaignTypeDtoArray);
        RequestDelegate createCampaignType = async context => await Write(context, store.SaveCampaignType(null, await Read(context, AppJsonContext.Default.CampaignTypeWriteRequest)), AppJsonContext.Default.CampaignTypeDto, 201);
        RequestDelegate updateCampaignType = async context => await Write(context, store.SaveCampaignType(Id(context), await Read(context, AppJsonContext.Default.CampaignTypeWriteRequest)), AppJsonContext.Default.CampaignTypeDto);
        RequestDelegate deleteCampaignType = context => { RequireDeleteConfirmation(context); store.DeleteCampaignType(Id(context)); return Deleted(context); };
        app.MapGet("/api/campaign-types", campaignTypes);
        app.MapPost("/api/campaign-types", createCampaignType);
        app.MapPut("/api/campaign-types/{id}", updateCampaignType);
        app.MapDelete("/api/campaign-types/{id}", deleteCampaignType);
        RequestDelegate campaigns = context => Write(context, store.ListCampaigns(new(Query(context, "q"), Query(context, "communityId"), Query(context, "typeId"),
            Query(context, "from"), Query(context, "to"), Number(context, "page", 1), Number(context, "pageSize", 30), Query(context, "pointId"))), AppJsonContext.Default.CampaignPage);
        RequestDelegate campaign = context => Write(context, store.GetCampaignDetail(Id(context)), AppJsonContext.Default.CampaignDetailDto);
        RequestDelegate createCampaign = async context => await Write(context, store.SaveCampaign(null, await Read(context, AppJsonContext.Default.CampaignWriteRequest)), AppJsonContext.Default.CampaignDetailDto, 201);
        RequestDelegate updateCampaign = async context => await Write(context, store.SaveCampaign(Id(context), await Read(context, AppJsonContext.Default.CampaignWriteRequest)), AppJsonContext.Default.CampaignDetailDto);
        RequestDelegate deleteCampaign = context => { RequireDeleteConfirmation(context); store.DeleteCampaign(Id(context)); return Deleted(context); };
        app.MapGet("/api/campaigns", campaigns);
        app.MapGet("/api/campaigns/{id}", campaign);
        app.MapPost("/api/campaigns", createCampaign);
        app.MapPut("/api/campaigns/{id}", updateCampaign);
        app.MapDelete("/api/campaigns/{id}", deleteCampaign);

        RequestDelegate issues = context => Write(context, store.ListIssues(new(Query(context, "status"), Query(context, "communityId"), Query(context, "pointId"), Query(context, "inspectionId"), Flag(context, "overdue"), Flag(context, "dueSoon"), Query(context, "from"), Query(context, "to"), Query(context, "q"), Number(context, "page", 1), Number(context, "pageSize", 30))), AppJsonContext.Default.IssuePage);
        RequestDelegate issue = context => Write(context, store.GetIssueDetail(Id(context)), AppJsonContext.Default.IssueDetailDto);
        RequestDelegate updateIssue = async context => await Write(context, store.UpdateIssue(Id(context), await Read(context, AppJsonContext.Default.IssueUpdateRequest)), AppJsonContext.Default.IssueDetailDto);
        RequestDelegate feedback = async context => await Write(context, store.RecordFeedback(Id(context), await Read(context, AppJsonContext.Default.FeedbackRequest)), AppJsonContext.Default.IssueDetailDto);
        RequestDelegate review = async context => await Write(context, store.ReviewAndClose(Id(context), await Read(context, AppJsonContext.Default.ReviewRequest)), AppJsonContext.Default.IssueDetailDto);
        RequestDelegate returnIssue = async context => await Write(context, store.ReturnIssue(Id(context), await Read(context, AppJsonContext.Default.ReturnIssueRequest)), AppJsonContext.Default.IssueDetailDto);
        app.MapGet("/api/issues", issues);
        app.MapGet("/api/issues/{id}", issue);
        app.MapPut("/api/issues/{id}", updateIssue);
        app.MapPost("/api/issues/{id}/feedback", feedback);
        app.MapPost("/api/issues/{id}/review", review);
        app.MapPost("/api/issues/{id}/return", returnIssue);
    }
}
