using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using StreetEcoTool;

namespace StreetEcoTool.Mobile;

/// <summary>
/// Mobile HTTP adapters reuse the same Store DTOs and SQLite mutation methods
/// as the desktop ASP.NET Core API; no separate Android database schema.
/// </summary>
internal static class MobileApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            AppJsonContext.Default, MapConfigJsonContext.Default, BackupJsonContext.Default,
            new DefaultJsonTypeInfoResolver()),
    };

    public static byte[] Serialize(object value) => JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Json);
    private static OfflineResponse Ok(object value, int status = 200) => OfflineResponse.Json(Serialize(value), status);
    private static OfflineResponse Deleted(Action action)
    {
        action();
        return OfflineResponse.Empty();
    }

    private static T Body<T>(OfflineRequest request) where T : class
    {
        if (request.Body.Length == 0) throw new ApiException(400, "缺少请求内容。");
        if (!request.Header("Content-Type").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(415, "请使用 JSON 提交数据。");
        return JsonSerializer.Deserialize<T>(request.Body, Json)
               ?? throw new ApiException(400, "提交内容不能为空。");
    }

    private static void RequireConfirmation(OfflineRequest request)
    {
        if (!request.Confirmed) throw new ApiException(400, "删除需要 confirm=true 确认。");
    }

    private static OfflineResponse Delete(OfflineRequest request, Action action)
    {
        RequireConfirmation(request);
        return Deleted(action);
    }

    private static string? Q(OfflineRequest r, string name) => r.Query(name);
    private static string[] Tags(OfflineRequest r) => r.QueryValues("tagId");
    private static InspectionQuery Inspections(OfflineRequest r) => new(
        Q(r, "pointId"), Q(r, "communityId"), Q(r, "from"), Q(r, "to"), Q(r, "q"),
        r.Number("page", 1), r.Number("pageSize", 30), Q(r, "rectify"),
        r.Flag("overdue"), r.Flag("dueSoon"), Q(r, "typeId"), r.Flag("temporary"),
        r.Flag("awaitingReview"), r.Flag("openOnly"), Q(r, "issueTypeId"), Tags(r),
        Q(r, "matterId"), r.Flag("map"));

    private static ActivityQuery Activities(OfflineRequest r) => new(
        Q(r, "q"), Q(r, "communityId"), Q(r, "typeId"), Q(r, "from"), Q(r, "to"),
        r.Number("page", 1), r.Number("pageSize", 30), Q(r, "pointId"), Q(r, "template"),
        Q(r, "remediationTypeId"), Tags(r), Q(r, "matterId"));

    public static Task<OfflineResponse> HandleAsync(Store store, OfflineRequest r, Uri origin, CancellationToken token)
    {
        // Domain mutations are synchronous; all disk/database access stays inside the Store's lock.
        return Task.FromResult(Route(store, r, origin));
    }

    private static OfflineResponse Route(Store store, OfflineRequest r, Uri origin)
    {
        var segments = r.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments[0] != "api")
            return OfflineResponse.Text("Not found", 404);
        var area = segments[1];
        var method = r.Method;
        if (segments.Length == 2)
        {
            return (area, method) switch
            {
                ("health", "GET") => Ok(new OkDto()),
                ("system", "GET") => Ok(new
                {
                    port = origin.Port, localUrl = origin.ToString(), addresses = new[] { origin.ToString() },
                    today = Store.Today, version = "Android .NET 11 CoreCLR",
                    listen = new[] { "127.0.0.1" }, interfaces = Array.Empty<object>(),
                    canConfigure = false, unavailableSaved = Array.Empty<string>(),
                    switching = false, lastError = (string?)null
                }),
                ("bootstrap", "GET") => Ok(store.GetBootstrap()),
                ("dashboard", "GET") => Ok(store.GetDashboard()),
                ("settings", "GET") => Ok(store.GetSettings()),
                ("settings", "PUT") => Ok(store.SaveSettings(Body<SettingsDto>(r))),
                ("communities", "GET") => Ok(store.GetCommunities()),
                ("communities", "POST") => Ok(store.SaveCommunity(null, Body<CommunityWriteRequest>(r)), 201),
                ("point-types", "GET") => Ok(store.GetPointTypes()),
                ("point-types", "POST") => Ok(store.SavePointType(null, Body<PointTypeWriteRequest>(r)), 201),
                ("issue-types", "GET") => Ok(store.GetIssueTypes()),
                ("issue-types", "POST") => Ok(store.SaveIssueType(null, Body<IssueTypeWriteRequest>(r)), 201),
                ("remediation-types", "GET") => Ok(store.GetCampaignTypes()),
                ("remediation-types", "POST") => Ok(store.SaveCampaignType(null, Body<CampaignTypeWriteRequest>(r)), 201),
                ("activity-types", "GET") => Ok(store.GetActivityTypes()),
                ("activity-types", "POST") => Ok(store.SaveActivityType(null, Body<ActivityTypeWriteRequest>(r)), 201),
                ("tags", "GET") => Ok(store.GetTags()),
                ("tags", "POST") => Ok(store.SaveTag(null, Body<TagWriteRequest>(r)), 201),
                ("points", "GET") => Ok(store.ListPoints(new PointQuery(
                    Q(r, "q"), Q(r, "communityId"), Q(r, "typeId"), Q(r, "disabled") ?? "false",
                    r.Number("page", 1), r.Number("pageSize", 30), r.Flag("recent"), r.Flag("missed"),
                    Q(r, "temporary") ?? "false", Tags(r), r.Flag("map"))),
                ("points", "POST") => Ok(store.SavePoint(null, Body<PointWriteRequest>(r)), 201),
                ("inspections", "GET") => Ok(store.ListInspections(Inspections(r))),
                ("inspections", "POST") => Ok(store.CreateInspection(Body<InspectionCreateRequest>(r)), 201),
                ("issues", "GET") => Ok(store.ListIssues(new IssueQuery(
                    Q(r, "status"), Q(r, "communityId"), Q(r, "pointId"), Q(r, "inspectionId"),
                    r.Flag("overdue"), r.Flag("dueSoon"), Q(r, "from"), Q(r, "to"), Q(r, "q"),
                    r.Number("page", 1), r.Number("pageSize", 30), Q(r, "typeId")))),
                ("activities", "GET") => Ok(store.ListActivities(Activities(r))),
                ("activities", "POST") => Ok(store.SaveActivity(null, Body<ActivityWriteRequest>(r)), 201),
                ("matters", "GET") => Ok(store.ListMatters(new MatterQuery(
                    Q(r, "q"), Q(r, "status"), Q(r, "source"), Q(r, "pointId"), Q(r, "from"),
                    Q(r, "to"), r.Flag("overdue"), r.Number("page", 1), r.Number("pageSize", 30), Tags(r)))),
                ("matters", "POST") => Ok(store.SaveMatter(null, Body<MatterWriteRequest>(r)), 201),
                ("backups", "GET") => Ok(store.GetBackups()),
                ("photos", "PUT") => Ok(store.UploadPhoto(r.Body, Q(r, "phase") ?? "site"), 201),
                _ => OfflineResponse.Text("API not found", 404)
            };
        }

        if (segments.Length >= 3 && area == "system" && segments[2] == "listen")
            throw new ApiException(403, "Android 离线服务只在 127.0.0.1 监听，不允许对外开放。");
        if (segments.Length >= 3 && area == "map")
            return MapApi(store, r, segments);
        if (segments.Length == 3 && area == "inspections" && segments[2] == "counts" && method == "GET")
            return Ok(store.GetInspectionCounts(Inspections(r)));
        if (segments.Length == 3 && area == "points" && segments[2] == "batch-delete" && method == "POST")
            return Delete(r, () => store.DeletePoints(Body<PointBulkDeleteRequest>(r).Ids));
        if (segments.Length == 3 && area == "backups" && segments[2] == "download" && method == "GET")
        {
            var file = store.CreateBackupZip();
            try { return new OfflineResponse(200, "application/zip", File.ReadAllBytes(file)); }
            finally { File.Delete(file); }
        }
        if (segments.Length == 3 && area == "photos" && method == "DELETE")
            return Delete(r, () => store.DeletePhoto(segments[2]));
        if (segments.Length == 3 && area == "photos" && method == "PUT")
            return OfflineResponse.Text("Not found", 404);

        if (segments.Length < 3) return OfflineResponse.Text("API not found", 404);
        var id = segments[2];

        if (segments.Length == 3)
        {
            return (area, method) switch
            {
                ("communities", "PUT") => Ok(store.SaveCommunity(id, Body<CommunityWriteRequest>(r))),
                ("communities", "DELETE") => Delete(r, () => store.DeleteCommunity(id)),
                ("point-types", "PUT") => Ok(store.SavePointType(id, Body<PointTypeWriteRequest>(r))),
                ("point-types", "DELETE") => Delete(r, () => store.DeletePointType(id)),
                ("issue-types", "PUT") => Ok(store.SaveIssueType(id, Body<IssueTypeWriteRequest>(r))),
                ("issue-types", "DELETE") => Delete(r, () => store.DeleteIssueType(id)),
                ("remediation-types", "PUT") => Ok(store.SaveCampaignType(id, Body<CampaignTypeWriteRequest>(r))),
                ("remediation-types", "DELETE") => Delete(r, () => store.DeleteCampaignType(id)),
                ("activity-types", "PUT") => Ok(store.SaveActivityType(id, Body<ActivityTypeWriteRequest>(r))),
                ("activity-types", "DELETE") => Delete(r, () => store.DeleteActivityType(id)),
                ("tags", "PUT") => Ok(store.SaveTag(id, Body<TagWriteRequest>(r))),
                ("tags", "DELETE") => Delete(r, () => store.DeleteTag(id)),
                ("points", "GET") => Ok(store.GetPointDetail(id)),
                ("points", "PUT") => Ok(store.SavePoint(id, Body<PointWriteRequest>(r))),
                ("points", "DELETE") => Delete(r, () => store.DeletePoint(id)),
                ("inspections", "GET") => Ok(store.GetInspectionDetail(id)),
                ("inspections", "PUT") => Ok(store.UpdateInspection(id, Body<InspectionUpdateRequest>(r))),
                ("inspections", "DELETE") => Delete(r, () => store.DeleteInspection(id)),
                ("issues", "GET") => Ok(store.GetIssueDetail(id)),
                ("issues", "PUT") => Ok(store.UpdateIssue(id, Body<IssueUpdateRequest>(r))),
                ("activities", "GET") => Ok(store.GetActivityDetail(id)),
                ("activities", "PUT") => Ok(store.SaveActivity(id, Body<ActivityWriteRequest>(r))),
                ("activities", "DELETE") => Delete(r, () => store.DeleteActivity(id)),
                ("matters", "GET") => Ok(store.GetMatterDetail(id)),
                ("matters", "PUT") => Ok(store.SaveMatter(id, Body<MatterWriteRequest>(r))),
                ("matters", "DELETE") => Delete(r, () => store.DeleteMatter(id)),
                _ => OfflineResponse.Text("API not found", 404)
            };
        }

        if (segments.Length == 4)
        {
            var action = segments[3];
            if (action == "tags" && method == "PUT" && area is "points" or "inspections" or "activities" or "matters")
            {
                var kind = area == "activities" ? "activity" : area[..^1];
                return Ok(store.SetTags(kind, id, Body<TagLinksRequest>(r).TagIds ??
                    throw new ApiException(400, "缺少标签列表。")));
            }
            if (action == "matters" && method == "PUT" && area is "inspections" or "issues" or "activities")
            {
                store.SetMatterLinks(area == "activities" ? "activity" : area[..^1], id,
                    Body<MatterLinksRequest>(r).MatterIds ?? throw new ApiException(400, "缺少事项列表。"));
                return Ok(new OkDto());
            }
            if (action == "relations" && method == "PUT" && area is "points" or "inspections" or "issues" or "activities" or "matters")
            {
                store.SetRelations(area == "activities" ? "activity" : area[..^1], id, Body<RecordRelationsRequest>(r));
                return Ok(new OkDto());
            }
            return (area, action, method) switch
            {
                ("points", "photos", "PUT") => Ok(store.AttachPointPhotos(id, Body<PointPhotosRequest>(r))),
                ("points", "status", "PUT") => Ok(store.SetPointDisabled(id, Body<PointStatusRequest>(r))),
                ("points", "promote", "POST") => Ok(store.PromotePoint(id, Body<PointPromoteRequest>(r))),
                ("inspections", "point", "POST") => Ok(store.ReassignInspectionPoint(id, Body<PointReassignRequest>(r))),
                ("inspections", "feedback", "POST") => Ok(store.RecordInspectionFeedback(id, Body<InspectionFeedbackRequest>(r))),
                ("inspections", "review", "POST") => Ok(store.ReviewInspection(id, Body<InspectionReviewRequest>(r))),
                ("issues", "feedback", "POST") => Ok(store.RecordFeedback(id, Body<FeedbackRequest>(r))),
                ("issues", "review", "POST") => Ok(store.ReviewAndClose(id, Body<ReviewRequest>(r))),
                ("issues", "return", "POST") => Ok(store.ReturnIssue(id, Body<ReturnIssueRequest>(r))),
                ("activities", "point", "POST") => Ok(store.ReassignActivityPoint(id, Body<PointReassignRequest>(r))),
                ("matters", "actions", "POST") => Ok(store.AddMatterAction(id, Body<MatterActionRequest>(r))),
                ("points", "attachments", "GET") => Ok(store.GetAttachments(id)),
                ("points", "attachments", "PUT") => Ok(store.UploadAttachment(id, Q(r, "name") ?? "attachment.bin", r.Body), 201),
                _ => OfflineResponse.Text("API not found", 404)
            };
        }
        if (segments.Length == 5 && area == "photos" && segments[3] == "thumb" && method == "PUT")
        {
            store.UploadThumbnail(id, r.Body);
            return OfflineResponse.Empty();
        }
        return OfflineResponse.Text("API not found", 404);
    }

    private static OfflineResponse MapApi(Store store, OfflineRequest r, string[] segments)
    {
        var method = r.Method;
        if (segments.Length == 3)
        {
            return (segments[2], method) switch
            {
                ("layers", "GET") => Ok(store.GetMapLayers()),
                ("settings", "GET") => Ok(store.GetMapSettings()),
                ("settings", "POST") => Ok(store.CreateMapProvider(Body<TileProvider>(r)), 201),
                _ => OfflineResponse.Text("API not found", 404)
            };
        }
        if (segments.Length == 4 && segments[2] == "settings" && segments[3] == "order" && method == "PUT")
        {
            store.ReorderMapProviders(Body<MapOrderRequest>(r));
            return Ok(store.GetMapSettings());
        }
        if (segments.Length < 4 || segments[2] != "settings")
            return OfflineResponse.Text("API not found", 404);
        var id = segments[3];
        if (segments.Length == 4)
        {
            return method switch
            {
                "PUT" => Ok(store.SaveMapProvider(id, Body<TileProvider>(r))),
                "DELETE" => Delete(r, () => store.DeleteMapProvider(id)),
                _ => OfflineResponse.Text("API not found", 404)
            };
        }
        if (segments.Length == 5)
        {
            return (segments[4], method) switch
            {
                ("check", "GET") => Ok(store.CheckMapLayerAsync(id, CancellationToken.None).GetAwaiter().GetResult()),
                ("enabled", "PUT") => Ok(store.SetMapProviderEnabled(id, Body<MapEnabledRequest>(r))),
                _ => OfflineResponse.Text("API not found", 404)
            };
        }
        return OfflineResponse.Text("API not found", 404);
    }
}
