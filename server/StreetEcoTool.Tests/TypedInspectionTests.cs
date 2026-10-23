using Xunit;

namespace StreetEcoTool.Tests;

public static class TypedInspectionTests
{
    // Existing test fixtures explicitly supply a current issue type without
    // introducing a permissive fallback into the production API.
    public static InspectionDetailDto CreateTypedInspection(this Store store, InspectionCreateRequest request)
    {
        var type = store.GetIssueTypes()[0].Id;
        return store.CreateInspection(request with
        {
            Issues = request.Issues?.Select(issue => issue with { IssueTypeId = type }).ToArray()
        });
    }
}
