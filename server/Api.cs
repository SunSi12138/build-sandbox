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
    private static string[] Ids(HttpContext context, string name) => context.Request.Query[name].Select(value => value ?? "").ToArray();
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

    internal static InspectionQuery InspectionFilters(HttpContext context) => new(Query(context, "pointId"), Query(context, "communityId"),
        Query(context, "from"), Query(context, "to"), Query(context, "q"), Number(context, "page", 1), Number(context, "pageSize", 30),
        Query(context, "rectify"), Flag(context, "overdue"), Flag(context, "dueSoon"), Query(context, "typeId"), Flag(context, "temporary"),
        Flag(context, "awaitingReview"), Flag(context, "openOnly"), Query(context, "issueTypeId"), Ids(context, "tagId"), Query(context, "matterId"));
    internal static CampaignQuery CampaignFilters(HttpContext context) => new(Query(context, "q"), Query(context, "communityId"), Query(context, "remediationTypeId"),
        Query(context, "from"), Query(context, "to"), PointId: Query(context, "pointId"), TagIds: Ids(context, "tagId"), MatterId: Query(context, "matterId"), ActivityTypeId: Query(context, "typeId"));
    internal static ActivityQuery ActivityFilters(HttpContext context) => new(Query(context, "q"), Query(context, "communityId"), Query(context, "typeId"),
        Query(context, "from"), Query(context, "to"), Number(context, "page", 1), Number(context, "pageSize", 30), Query(context, "pointId"),
        Query(context, "template"), Query(context, "remediationTypeId"), Ids(context, "tagId"), Query(context, "matterId"));

    public static void Map(WebApplication app, Store store)
    {
        RequestDelegate health = context => Write(context, new OkDto(), AppJsonContext.Default.OkDto);
        RequestDelegate bootstrap = context => Write(context, store.GetBootstrap(), AppJsonContext.Default.BootstrapDto);
        RequestDelegate getSettings = context => Write(context, store.GetSettings(), AppJsonContext.Default.SettingsDto);
        RequestDelegate saveSettings = async context => await Write(context, store.SaveSettings(await Read(context, AppJsonContext.Default.SettingsDto)), AppJsonContext.Default.SettingsDto);
        RequestDelegate dashboard = context => Write(context, store.GetDashboard(), AppJsonContext.Default.DashboardDto);
        RequestDelegate issueTypes = context => Write(context, store.GetIssueTypes(), AppJsonContext.Default.IssueTypeDtoArray);
        RequestDelegate addIssueType = async context => await Write(context, store.SaveIssueType(null, await Read(context, AppJsonContext.Default.IssueTypeWriteRequest)), AppJsonContext.Default.IssueTypeDto, 201);
        RequestDelegate changeIssueType = async context => await Write(context, store.SaveIssueType(Id(context), await Read(context, AppJsonContext.Default.IssueTypeWriteRequest)), AppJsonContext.Default.IssueTypeDto);
        RequestDelegate deleteIssueType = context => { RequireDeleteConfirmation(context); store.DeleteIssueType(Id(context)); return Deleted(context); };
        app.MapGet("/api/issue-types", issueTypes);
        app.MapPost("/api/issue-types", addIssueType);
        app.MapPut("/api/issue-types/{id}", changeIssueType);
        app.MapDelete("/api/issue-types/{id}", deleteIssueType);
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
            Query(context, "disabled") ?? "false", Number(context, "page", 1), Number(context, "pageSize", 30), Flag(context, "recent"), Flag(context, "missed"), Query(context, "temporary") ?? "false", Ids(context, "tagId"))), AppJsonContext.Default.PointPage);
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
        app.MapGet("/api/remediation-types", campaignTypes);
        app.MapPost("/api/remediation-types", createCampaignType);
        app.MapPut("/api/remediation-types/{id}", updateCampaignType);
        app.MapDelete("/api/remediation-types/{id}", deleteCampaignType);
        RequestDelegate activityTypes = context => Write(context, store.GetActivityTypes(), AppJsonContext.Default.ActivityTypeDtoArray);
        RequestDelegate addActivityType = async context => await Write(context, store.SaveActivityType(null, await Read(context, AppJsonContext.Default.ActivityTypeWriteRequest)), AppJsonContext.Default.ActivityTypeDto, 201);
        RequestDelegate changeActivityType = async context => await Write(context, store.SaveActivityType(Id(context), await Read(context, AppJsonContext.Default.ActivityTypeWriteRequest)), AppJsonContext.Default.ActivityTypeDto);
        RequestDelegate deleteActivityType = context => { RequireDeleteConfirmation(context); store.DeleteActivityType(Id(context)); return Deleted(context); };
        app.MapGet("/api/activity-types", activityTypes);
        app.MapPost("/api/activity-types", addActivityType);
        app.MapPut("/api/activity-types/{id}", changeActivityType);
        app.MapDelete("/api/activity-types/{id}", deleteActivityType);
        RequestDelegate activities = context => Write(context, store.ListActivities(ActivityFilters(context)), AppJsonContext.Default.ActivityPage);
        RequestDelegate activity = context => Write(context, store.GetActivityDetail(Id(context)), AppJsonContext.Default.ActivityDetailDto);
        RequestDelegate addActivity = async context => await Write(context, store.SaveActivity(null, await Read(context, AppJsonContext.Default.ActivityWriteRequest)), AppJsonContext.Default.ActivityDetailDto, 201);
        RequestDelegate changeActivity = async context => await Write(context, store.SaveActivity(Id(context), await Read(context, AppJsonContext.Default.ActivityWriteRequest)), AppJsonContext.Default.ActivityDetailDto);
        RequestDelegate deleteActivity = context => { RequireDeleteConfirmation(context); store.DeleteActivity(Id(context)); return Deleted(context); };
        app.MapGet("/api/activities", activities);
        app.MapPost("/api/activities", addActivity);
        app.MapGet("/api/activities/{id}", activity);
        app.MapPut("/api/activities/{id}", changeActivity);
        app.MapDelete("/api/activities/{id}", deleteActivity);

        RequestDelegate tags = context => Write(context, store.GetTags(), AppJsonContext.Default.TagDtoArray);
        RequestDelegate addTag = async context => await Write(context, store.SaveTag(null, await Read(context, AppJsonContext.Default.TagWriteRequest)), AppJsonContext.Default.TagDto, 201);
        RequestDelegate updateTag = async context => await Write(context, store.SaveTag(Id(context), await Read(context, AppJsonContext.Default.TagWriteRequest)), AppJsonContext.Default.TagDto);
        RequestDelegate deleteTag = context => { RequireDeleteConfirmation(context); store.DeleteTag(Id(context)); return Deleted(context); };
        app.MapGet("/api/tags", tags); app.MapPost("/api/tags", addTag); app.MapPut("/api/tags/{id}", updateTag); app.MapDelete("/api/tags/{id}", deleteTag);
        foreach (var kind in new[] { "point", "inspection", "activity", "matter" })
        {
            RequestDelegate linkTags = async context => { var body = await Read(context, AppJsonContext.Default.TagLinksRequest); await Write(context, store.SetTags(kind, Id(context), body.TagIds ?? throw new ApiException(400, "缺少标签列表。")), AppJsonContext.Default.TagDtoArray); };
            app.MapPut($"/api/{(kind == "activity" ? "activities" : kind + "s")}/{{id}}/tags", linkTags);
        }
        foreach (var kind in new[] { "inspection", "issue", "activity" })
        {
            var route = kind == "activity" ? "activities" : kind + "s";
            RequestDelegate links = async context => { var body = await Read(context, AppJsonContext.Default.MatterLinksRequest); store.SetMatterLinks(kind, Id(context), body.MatterIds ?? throw new ApiException(400, "缺少事项列表。")); await Write(context, new OkDto(), AppJsonContext.Default.OkDto); };
            app.MapPut($"/api/{route}/{{id}}/matters", links);
        }
        foreach (var kind in new[] { "point", "inspection", "issue", "activity", "matter" })
        {
            var route = kind == "activity" ? "activities" : kind + "s";
            RequestDelegate relations = async context => { var body = await Read(context, AppJsonContext.Default.RecordRelationsRequest); store.SetRelations(kind, Id(context), body); await Write(context, new OkDto(), AppJsonContext.Default.OkDto); };
            app.MapPut($"/api/{route}/{{id}}/relations", relations);
        }
        RequestDelegate matters = context => Write(context, store.ListMatters(new(Query(context, "q"), Query(context, "status"), Query(context, "source"), Query(context, "pointId"), Query(context, "from"), Query(context, "to"), Flag(context, "overdue"), Number(context, "page", 1), Number(context, "pageSize", 30), Ids(context, "tagId"))), AppJsonContext.Default.MatterPage);
        RequestDelegate matter = context => Write(context, store.GetMatterDetail(Id(context)), AppJsonContext.Default.MatterDetailDto);
        RequestDelegate addMatter = async context => await Write(context, store.SaveMatter(null, await Read(context, AppJsonContext.Default.MatterWriteRequest)), AppJsonContext.Default.MatterDto, 201);
        RequestDelegate updateMatter = async context => await Write(context, store.SaveMatter(Id(context), await Read(context, AppJsonContext.Default.MatterWriteRequest)), AppJsonContext.Default.MatterDto);
        RequestDelegate action = async context => await Write(context, store.AddMatterAction(Id(context), await Read(context, AppJsonContext.Default.MatterActionRequest)), AppJsonContext.Default.MatterDetailDto);
        RequestDelegate deleteMatter = context => { RequireDeleteConfirmation(context); store.DeleteMatter(Id(context)); return Deleted(context); };
        app.MapGet("/api/matters", matters); app.MapPost("/api/matters", addMatter); app.MapGet("/api/matters/{id}", matter);
        app.MapPut("/api/matters/{id}", updateMatter); app.MapDelete("/api/matters/{id}", deleteMatter); app.MapPost("/api/matters/{id}/actions", action);
        store.MapAttachmentApi(app);

        RequestDelegate issues = context => Write(context, store.ListIssues(new(Query(context, "status"), Query(context, "communityId"), Query(context, "pointId"), Query(context, "inspectionId"), Flag(context, "overdue"), Flag(context, "dueSoon"), Query(context, "from"), Query(context, "to"), Query(context, "q"), Number(context, "page", 1), Number(context, "pageSize", 30), Query(context, "typeId"))), AppJsonContext.Default.IssuePage);
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
