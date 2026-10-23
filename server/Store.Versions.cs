namespace StreetEcoTool;

public sealed partial class Store
{
    private static void CheckRevision(int? expected, int current)
    {
        if (expected is null) throw new ApiException(409, "请刷新记录后再操作。");
        if (expected != current) throw new ApiException(409, "这条资料刚被其他人修改，请保留当前内容，刷新后核对再保存。");
    }
}
