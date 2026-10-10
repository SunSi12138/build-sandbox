using Xunit;

namespace StreetEcoTool.Tests;

public sealed class PhotoStorageTests
{
    private const string Id = "5ea78a63d63747f5bf3a0b74d9632ccc";
    private static string Root => Path.Combine(Path.GetTempPath(), "hbtool-photo-path-tests", "photos");

    [Theory]
    [InlineData("202601")]
    [InlineData("202612")]
    public void ResolvesOnlyTheCanonicalMonthAndMatchingId(string month)
    {
        Assert.Equal(Path.Combine(Path.GetFullPath(Root), month, Id + ".jpg"),
            PhotoStorage.ResolvePath(Root, Id, month + "/" + Id + ".jpg"));
    }

    [Theory]
    [InlineData("202600/{id}.jpg")]
    [InlineData("202613/{id}.jpg")]
    [InlineData("000001/{id}.jpg")]
    [InlineData("../{id}.jpg")]
    [InlineData("202610/../../{id}.jpg")]
    [InlineData("202610\\{id}.jpg")]
    [InlineData("/202610/{id}.jpg")]
    [InlineData("202610/{id}.jpeg")]
    [InlineData("202610/00000000000000000000000000000000.jpg")]
    [InlineData("202610/{id}.jpg/extra")]
    public void RejectsTraversalInvalidMonthAndMismatchedFilename(string path)
    {
        Assert.Throws<InvalidDataException>(() => PhotoStorage.ResolvePath(Root, Id, path.Replace("{id}", Id)));
    }

    [Fact]
    public void RejectsNonCanonicalPhotoIds()
    {
        var uppercase = Id.ToUpperInvariant();
        Assert.Throws<InvalidDataException>(() => PhotoStorage.ResolvePath(Root, uppercase, "202610/" + uppercase + ".jpg"));
        Assert.Throws<InvalidDataException>(() => PhotoStorage.ResolvePath(Root, "../" + Id, "202610/" + Id + ".jpg"));
    }
}
