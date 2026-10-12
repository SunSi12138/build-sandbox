using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class SourceCopyTests
{
    private static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "web", "app"))) return directory.FullName;
        throw new DirectoryNotFoundException("找不到待检查的源代码");
    }

    [Fact]
    public void UserFacingSourceHasNoProhibitedCopy()
    {
        string[] banned = ["留痕", "认真", "妥善", "随手", "有迹可循", "完整记录", "记得", "让", "助力", "轻松", "一键", "每一次", "每一项", "每一步", "工作档案", "按期闭环"];
        var root = Repository();
        var files = Directory.EnumerateFiles(Path.Combine(root, "server"), "*.cs", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "web", "app"), "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) is ".vue" or ".ts" or ".css"));
        var violations = new List<string>();
        foreach (var file in files)
        {
            var source = WithoutComments(File.ReadAllText(file));
            violations.AddRange(banned.Where(word => source.Contains(word, StringComparison.Ordinal)).Select(word => Path.GetRelativePath(root, file) + ": " + word));
        }
        Assert.Empty(violations);
    }

    [Fact]
    public void PagesAndComponentsUseColorTokensAndCompactControls()
    {
        var app = Path.Combine(Repository(), "web", "app");
        foreach (var folder in new[] { "pages", "components" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(app, folder), "*.vue", SearchOption.AllDirectories))
                Assert.False(Regex.IsMatch(WithoutComments(File.ReadAllText(file)), @"#[0-9a-fA-F]{3,8}\b"), file);
        Assert.DoesNotContain("min-h-12", File.ReadAllText(Path.Combine(app, "app.config.ts")), StringComparison.Ordinal);
    }

    // Keep quoted URLs and their text intact while ignoring code and HTML comments.
    private static string WithoutComments(string source)
    {
        var result = new StringBuilder(source.Length);
        char quote = '\0';
        for (var i = 0; i < source.Length; i++)
        {
            var value = source[i];
            if (quote != '\0')
            {
                result.Append(value);
                if (value == '\\' && i + 1 < source.Length) result.Append(source[++i]);
                else if (value == quote) quote = '\0';
                continue;
            }
            if (value is '\'' or '"' or '`') { quote = value; result.Append(value); continue; }
            if (source.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = source.IndexOf("-->", i + 4, StringComparison.Ordinal); i = end < 0 ? source.Length : end + 2; continue;
            }
            if (source.AsSpan(i).StartsWith("/*", StringComparison.Ordinal))
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? source.Length : end + 1; continue;
            }
            if (source.AsSpan(i).StartsWith("//", StringComparison.Ordinal))
            {
                while (i + 1 < source.Length && source[i + 1] is not '\r' and not '\n') i++;
                continue;
            }
            result.Append(value);
        }
        return result.ToString();
    }
}
