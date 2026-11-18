using Xunit;
using System.IO.Compression;
using System.Text.Json;

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
            Longitude: 88.893426, Latitude: 29.274331, Revision: point.Revision));
        var photo = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        var associated = store.AttachPointPhotos(point.Id, new([photo.Id]));
        Assert.Equal(newer with { PhotoCount = 1, CoverPhotoId = photo.Id }, associated);
        var reactivated = store.SetPointDisabled(point.Id, new(false, Revision: associated.Revision));
        Assert.False(reactivated.Disabled);
        Assert.Equal(associated.Revision + 1, reactivated.Revision);
        Assert.Equal(reactivated, store.AttachPointPhotos(point.Id, new([photo.Id])));
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
    public void PointPhotoRemovalIsTransactionalAndCannotLeaveDanglingFieldImages()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var type = store.GetPointTypes()[0];
        store.SavePointType(type.Id, new(type.Name, [new("license", "许可证", "image", false)]));
        var point = environment.CreatePoint(store);
        var gallery = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        var license = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        using var fieldJson = JsonDocument.Parse(JsonSerializer.Serialize(new { license = license.Id }));
        var extra = fieldJson.RootElement.Clone();
        var original = store.SavePoint(point.Id, new(point.Name, point.CommunityId, point.TypeId, point.Address,
            ExtraValues: extra, PhotoIds: [gallery.Id, license.Id], Revision: point.Revision));
        Assert.Equal(2, original.PhotoCount);
        // Closing the editor without sending a save leaves the saved picture intact.
        using (var beforeCancel = store.OpenPhoto(gallery.Id)) Assert.True(beforeCancel.Length > 0);
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.DeletePhoto(license.Id)).StatusCode);
        var removeGallery = new PointWriteRequest(original.Name, original.CommunityId, original.TypeId,
            original.Address, ExtraValues: extra, PhotoIds: [license.Id], Revision: original.Revision);
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.SavePoint(point.Id,
            removeGallery with { Revision = point.Revision })).StatusCode);
        using (var afterFailedSave = store.OpenPhoto(gallery.Id)) Assert.True(afterFailedSave.Length > 0);
        Assert.Equal(2, store.GetPoint(point.Id).PhotoCount);
        var saved = store.SavePoint(point.Id, removeGallery);
        Assert.Equal(1, saved.PhotoCount);
        Assert.Equal(license.Id, Assert.Single(store.GetPointDetail(point.Id).Photos!).Id);
        Assert.Equal(404, Assert.Throws<ApiException>(() => store.OpenPhoto(gallery.Id)).StatusCode);
        Assert.Equal(license.Id, saved.ExtraValues!.Value.GetProperty("license").GetString());
        using (var docx = ZipFile.OpenRead(store.ExportPlaceWord(point.Id).FilePath))
            Assert.Single(docx.Entries, entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal));
        V2Environment.AssertForeignKeys(store.DatabasePath);
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
