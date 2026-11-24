using System.Text;

namespace S3Lite.Tests;

public sealed class ObjectStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly ObjectStore _store;

    public ObjectStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "s3lite-tests-" + Guid.NewGuid().ToString("N"));
        _store = new ObjectStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string Text(Stream s) => new StreamReader(s).ReadToEnd();

    [Fact]
    public void PutAndGetRoundtrip()
    {
        _store.CreateBucket("photos");
        _store.PutObject("photos", "hello.txt", new MemoryStream(Bytes("hello")), "text/plain");
        var got = _store.GetObject("photos", "hello.txt");
        using var content = got.Content;
        var version = got.Version;
        Assert.Equal("hello", Text(content));
        Assert.Equal(5, version.Size);
        Assert.Equal("text/plain", version.ContentType);
    }

    [Fact]
    public void PutToMissingBucketThrowsNoSuchBucket()
    {
        var ex = Assert.Throws<S3Exception>(() =>
            _store.PutObject("nope", "k", new MemoryStream(Bytes("x")), "text/plain"));
        Assert.Equal("NoSuchBucket", ex.Code);
        Assert.Equal(404, ex.HttpStatus);
    }

    [Fact]
    public void CreateDuplicateBucketThrows()
    {
        _store.CreateBucket("dup");
        var ex = Assert.Throws<S3Exception>(() => _store.CreateBucket("dup"));
        Assert.Equal("BucketAlreadyExists", ex.Code);
    }

    [Fact]
    public void DeleteNonEmptyBucketThrows()
    {
        _store.CreateBucket("full");
        _store.PutObject("full", "k", new MemoryStream(Bytes("x")), "text/plain");
        var ex = Assert.Throws<S3Exception>(() => _store.DeleteBucket("full"));
        Assert.Equal("BucketNotEmpty", ex.Code);
    }

    [Fact]
    public void DeleteEmptyBucketWorks()
    {
        _store.CreateBucket("gone");
        _store.DeleteBucket("gone");
        Assert.DoesNotContain(_store.ListBuckets(), b => b.Name == "gone");
    }

    [Fact]
    public void ListBucketsIsSorted()
    {
        _store.CreateBucket("zeta");
        _store.CreateBucket("alpha");
        _store.CreateBucket("mid");
        var names = _store.ListBuckets().Select(b => b.Name).ToList();
        Assert.Equal(new[] { "alpha", "mid", "zeta" }, names);
    }

    [Fact]
    public void OverwriteWithoutVersioningKeepsSingleVersion()
    {
        _store.CreateBucket("bucket");
        _store.PutObject("bucket", "k", new MemoryStream(Bytes("one")), "text/plain");
        _store.PutObject("bucket", "k", new MemoryStream(Bytes("two")), "text/plain");
        var versions = _store.ListVersions("bucket", null);
        Assert.Single(versions);
        using var content = _store.GetObject("bucket", "k").Content;
        Assert.Equal("two", Text(content));
    }

    [Fact]
    public void VersioningKeepsBothVersionsAndLatestWins()
    {
        _store.CreateBucket("bucket");
        _store.SetVersioning("bucket", true);
        var v1 = _store.PutObject("bucket", "k", new MemoryStream(Bytes("one")), "text/plain");
        var v2 = _store.PutObject("bucket", "k", new MemoryStream(Bytes("two")), "text/plain");
        Assert.NotEqual(v1.VersionId, v2.VersionId);

        using var latestStream = _store.GetObject("bucket", "k").Content;
        Assert.Equal("two", Text(latestStream));
        using var oldStream = _store.GetObject("bucket", "k", v1.VersionId).Content;
        Assert.Equal("one", Text(oldStream));
        Assert.Equal(2, _store.ListVersions("bucket", null).Count);
    }

    [Fact]
    public void DeleteWithVersioningAddsDeleteMarkerAndGetFails()
    {
        _store.CreateBucket("bucket");
        _store.SetVersioning("bucket", true);
        _store.PutObject("bucket", "k", new MemoryStream(Bytes("one")), "text/plain");
        _store.DeleteObject("bucket", "k");
        var ex = Assert.Throws<S3Exception>(() => _store.GetObject("bucket", "k"));
        Assert.Equal("NoSuchKey", ex.Code);
        var versions = _store.ListVersions("bucket", null);
        Assert.Equal(2, versions.Count);
        Assert.True(versions[0].IsDeleteMarker);
        Assert.True(versions[0].IsLatest);
    }

    [Fact]
    public void DeleteSpecificVersionRestoresPreviousAsLatest()
    {
        _store.CreateBucket("bucket");
        _store.SetVersioning("bucket", true);
        var v1 = _store.PutObject("bucket", "k", new MemoryStream(Bytes("one")), "text/plain");
        var v2 = _store.PutObject("bucket", "k", new MemoryStream(Bytes("two")), "text/plain");
        _store.DeleteObject("bucket", "k", v2.VersionId);
        var got = _store.GetObject("bucket", "k");
        using var content = got.Content;
        Assert.Equal("one", Text(content));
        Assert.Equal(v1.VersionId, got.Version.VersionId);
    }

    [Fact]
    public void ListObjectsPrefixAndDelimiter()
    {
        _store.CreateBucket("bucket");
        foreach (var key in new[] { "a/1.txt", "a/2.txt", "b/1.txt", "top.txt" })
            _store.PutObject("bucket", key, new MemoryStream(Bytes("x")), "text/plain");

        var (objects, prefixes, next) = _store.ListObjects("bucket", "a/", "/", 1000, null);
        Assert.Equal(2, objects.Count);
        Assert.Empty(prefixes);
        Assert.Null(next);

        var (all, topPrefixes, _) = _store.ListObjects("bucket", null, "/", 1000, null);
        Assert.Single(all); // only top.txt is a direct child
        Assert.Equal("top.txt", all[0].Key);
        Assert.Equal(new[] { "a/", "b/" }, topPrefixes);
    }

    [Fact]
    public void ListObjectsPagination()
    {
        _store.CreateBucket("bucket");
        for (var i = 0; i < 5; i++)
            _store.PutObject("bucket", $"k{i:00}", new MemoryStream(Bytes("x")), "text/plain");

        var (page1, _, next1) = _store.ListObjects("bucket", null, null, 2, null);
        Assert.Equal(2, page1.Count);
        Assert.NotNull(next1);
        var (page2, _, next2) = _store.ListObjects("bucket", null, null, 2, next1);
        Assert.Equal(2, page2.Count);
        Assert.NotNull(next2);
        var (page3, _, next3) = _store.ListObjects("bucket", null, null, 2, next2);
        Assert.Single(page3);
        Assert.Null(next3);
        var keys = page1.Concat(page2).Concat(page3).Select(o => o.Key).ToList();
        Assert.Equal(new[] { "k00", "k01", "k02", "k03", "k04" }, keys);
    }

    [Fact]
    public void SameBytesStoredOnce()
    {
        _store.CreateBucket("bucket");
        _store.PutObject("bucket", "k1", new MemoryStream(Bytes("same")), "text/plain");
        _store.PutObject("bucket", "k2", new MemoryStream(Bytes("same")), "text/plain");
        var blobs = Directory.GetFiles(Path.Combine(_dir, "blobs"), "*", SearchOption.AllDirectories);
        Assert.Single(blobs);
    }

    [Fact]
    public void CopySharesBlob()
    {
        _store.CreateBucket("bucket");
        _store.PutObject("bucket", "src", new MemoryStream(Bytes("data")), "text/plain");
        _store.CopyObject("bucket", "src", "bucket", "dst");
        using var content = _store.GetObject("bucket", "dst").Content;
        Assert.Equal("data", Text(content));
        var blobs = Directory.GetFiles(Path.Combine(_dir, "blobs"), "*", SearchOption.AllDirectories);
        Assert.Single(blobs);
    }

    [Fact]
    public void DeleteVersionFreesBlobWhenUnreferenced()
    {
        _store.CreateBucket("bucket");
        _store.SetVersioning("bucket", true);
        var v = _store.PutObject("bucket", "k", new MemoryStream(Bytes("gone")), "text/plain");
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "blobs"), "*", SearchOption.AllDirectories));
        _store.DeleteObject("bucket", "k", v.VersionId);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "blobs"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void PruneKeepsNNoncurrentVersions()
    {
        _store.CreateBucket("bucket");
        _store.SetVersioning("bucket", true);
        for (var i = 0; i < 4; i++)
            _store.PutObject("bucket", "k", new MemoryStream(Bytes($"v{i}")), "text/plain");
        Assert.Equal(4, _store.ListVersions("bucket", null).Count);
        var removed = _store.PruneVersions("bucket", keep: 1);
        Assert.Equal(2, removed);
        var remaining = _store.ListVersions("bucket", null);
        Assert.Equal(2, remaining.Count);
        Assert.True(remaining[0].IsLatest);
        using var content = _store.GetObject("bucket", "k").Content;
        Assert.Equal("v3", Text(content));
    }

    [Fact]
    public void InvalidBucketNameRejected()
    {
        var ex = Assert.Throws<S3Exception>(() => _store.CreateBucket("AB"));
        Assert.Equal("InvalidBucketName", ex.Code);
        Assert.Throws<S3Exception>(() => _store.CreateBucket("has space"));
        Assert.Throws<S3Exception>(() => _store.CreateBucket("-starts-with-dash"));
    }

    [Fact]
    public void UserMetadataRoundtrip()
    {
        _store.CreateBucket("bucket");
        _store.PutObject("bucket", "k", new MemoryStream(Bytes("x")), "text/plain",
            new Dictionary<string, string> { ["author"] = "abrar", ["project"] = "s3lite" });
        var version = _store.HeadObject("bucket", "k");
        Assert.Equal("abrar", version.UserMetadata["author"]);
        Assert.Equal("s3lite", version.UserMetadata["project"]);
    }

    [Fact]
    public void HeadMissingKeyThrowsNoSuchKey()
    {
        _store.CreateBucket("bucket");
        var ex = Assert.Throws<S3Exception>(() => _store.HeadObject("bucket", "missing"));
        Assert.Equal("NoSuchKey", ex.Code);
    }
}
