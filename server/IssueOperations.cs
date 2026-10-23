using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreetEcoTool;
public sealed record IssueOperation(string Kind, string Date, string Notes, string? Deadline = null, string? ResponsibleUnit = null, string? Follower = null, string[]? PhotoIds = null);
[JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IssueOperation))]
public partial class IssueOperationJsonContext : JsonSerializerContext;
public sealed partial class Store
{
    public IssueDetailDto OperateIssue(string id, IssueOperation request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var issue=GetIssue(id);
            if(issue.Status=="closed")throw new ApiException(409,"该问题已经完成，如需继续办理请先退回。");
            if(request.Kind is not("progress" or "transfer" or "extend" or "assign"))throw new ApiException(400,"请选择办理方式。");
            var date=ActionDate(request.Date,issue.InspectionDate,"办理日期");
            var notes=Text(request.Notes,"办理说明",8000,true);
            var deadline=request.Kind=="extend"?DateText(request.Deadline,"新的期限"):issue.Deadline;
            if(request.Kind=="extend"&&string.CompareOrdinal(deadline,issue.Deadline)<=0)throw new ApiException(400,"新的期限应晚于原期限。");
            var responsible=request.Kind=="transfer"?Text(request.ResponsibleUnit,"接收单位",200,true):issue.ResponsibleUnit;
            var action=request.Kind switch{"progress"=>"跟进记录","transfer"=>"转交单位","extend"=>"调整期限",_=>"安排跟进人员"};
            var operation=request with {Date=date,Notes=notes,Deadline=deadline,ResponsibleUnit=responsible,Follower=Text(request.Follower,"跟进人员",200)};
            using var transaction=_db.BeginTransaction();
            using(var update=Command("UPDATE issues SET deadline=$deadline,responsible_unit=$unit WHERE id=$id;",transaction,("$deadline",deadline),("$unit",responsible),("$id",id)))update.ExecuteNonQuery();
            AddCommunityHistory("issue",id,JsonSerializer.Serialize(issue,AppJsonContext.Default.IssueDto),JsonSerializer.Serialize(operation,IssueOperationJsonContext.Default.IssueOperation),action,transaction);
            AttachPhotos(request.PhotoIds,"site",null,id,transaction);
            transaction.Commit();
            return GetIssueDetail(id);
        }
    }
    public void MapIssueOperations(WebApplication app)
    {
        RequestDelegate apply=async c=>await Api.Write(c,OperateIssue(Api.Id(c),await Api.Read(c,IssueOperationJsonContext.Default.IssueOperation)),AppJsonContext.Default.IssueDetailDto);
        app.MapPost("/api/issues/{id}/operations",apply);
    }
}
