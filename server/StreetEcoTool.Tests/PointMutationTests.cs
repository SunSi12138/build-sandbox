using Xunit;

namespace StreetEcoTool.Tests;

public sealed class PointMutationTests
{
    [Fact]
    public void PhotoAndStatusWritesCannotReplayAnOldPointSnapshot()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var newer = store.SavePoint(point.Id, new("新名称", point.CommunityId, point.TypeId, "新地址",
            Contact: "新联系人", Phone: "新电话", Frequency: "monthly", Notes: "新备注", Disabled: true,
            Longitude: 88.893426, Latitude: 29.274331));
        var photo = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        var associated = store.AttachPointPhotos(point.Id, new([photo.Id]));
        Assert.Equal(newer with { PhotoCount = 1, CoverPhotoId = photo.Id }, associated);
        Assert.Equal(associated with { Disabled = false }, store.SetPointDisabled(point.Id, new(false)));
        Assert.Equal(associated with { Disabled = false }, store.AttachPointPhotos(point.Id, new([photo.Id])));
        Assert.Throws<ApiException>(() => store.SetPointDisabled(point.Id, new(null)));
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public async Task ConcurrentPhotoCompletionsKeepBothAssociations()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var a = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        var b = store.UploadPhoto(WordExporterTests.Jpeg(80, 120), "point");
        await Task.WhenAll(Task.Run(() => store.AttachPointPhotos(point.Id, new([a.Id]))),
            Task.Run(() => store.AttachPointPhotos(point.Id, new([a.Id, b.Id]))));
        Assert.Equal(2, store.GetPoint(point.Id).PhotoCount);
        Assert.Equal(a.Id, store.GetPoint(point.Id).CoverPhotoId);
        Assert.Equal(new[] { a.Id, b.Id }, store.GetPointDetail(point.Id).Photos!.Select(photo => photo.Id));
    }

    [Fact]
    public void InvalidPhotoListsRollBackWithoutChangingPointMetadataOrOwnership()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var community = store.SaveCommunity(null, new("另一社区", "另一社区居委会"));
        var other = store.SavePoint(null, new("另一点位", community.Id, point.TypeId, "另一地址"));
        var photos = Enumerable.Range(0, 10).Select(_ => store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point")).ToArray();
        var wrongPhase = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "site");
        Assert.Throws<ApiException>(() => store.AttachPointPhotos(point.Id, new([photos[0].Id, photos[0].Id])));
        Assert.Throws<ApiException>(() => store.AttachPointPhotos(point.Id, new([photos[0].Id, wrongPhase.Id])));
        Assert.Throws<ApiException>(() => store.AttachPointPhotos(point.Id, new(photos.Select(photo => photo.Id).ToArray())));
        Assert.Equal(point, store.GetPoint(point.Id));
        store.AttachPointPhotos(other.Id, new([photos[9].Id]));
        Assert.Throws<ApiException>(() => store.AttachPointPhotos(point.Id, new([photos[0].Id, photos[9].Id])));
        Assert.Equal(point, store.GetPoint(point.Id));
        Assert.Equal(1, store.GetPoint(other.Id).PhotoCount);
        store.AttachPointPhotos(point.Id, new(photos.Take(9).Select(photo => photo.Id).ToArray()));
        Assert.Equal(9, store.GetPoint(point.Id).PhotoCount);
        Assert.Throws<ApiException>(() => store.AttachPointPhotos("missing", new([])));
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }
}
