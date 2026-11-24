using System.Security.Cryptography;
using System.Text;

namespace S3Lite.Tests;

public sealed class MultipartTests : IDisposable
{
    private readonly string _dir;
    private readonly ObjectStore _store;

    public MultipartTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "s3lite-mp-" + Guid.NewGuid().ToString("N"));
        // Small minimum part size so tests don't need 5 MiB parts.
        _store = new ObjectStore(_dir, new ObjectStoreOptions { MinPartSizeBytes = 10 });
        _store.CreateBucket("bucket");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void PartsUploadedOutOfOrderAssembleInOrder()
    {
        var upload = _store.InitiateMultipart("bucket", "big.bin", "application/octet-stream");
        var p2 = _store.UploadPart(upload.UploadId, 2, new MemoryStream(Bytes("0123456789ABCDE"))); // 15 bytes
        var p1 = _store.UploadPart(upload.UploadId, 1, new MemoryStream(Bytes("0123456789ABCDE")));
        var p3 = _store.UploadPart(upload.UploadId, 3, new MemoryStream(Bytes("END")));
        var version = _store.CompleteMultipart(upload.UploadId,
            new[] { (1, p1.ETag), (2, p2.ETag), (3, p3.ETag) });
        Assert.Equal(33, version.Size);
        Assert.EndsWith("-3", version.ETag);
        using var content = _store.GetObject("bucket", "big.bin").Content;
        Assert.Equal("0123456789ABCDE0123456789ABCDEEND", new StreamReader(content).ReadToEnd());
    }

    [Fact]
    public void MultipartETagIsMd5OfPartMd5s()
    {
        var upload = _store.InitiateMultipart("bucket", "f", "application/octet-stream");
        var p1 = _store.UploadPart(upload.UploadId, 1, new MemoryStream(Bytes("0123456789ABCDE")));
        var p2 = _store.UploadPart(upload.UploadId, 2, new MemoryStream(Bytes("0123456789ABCDE")));
        var version = _store.CompleteMultipart(upload.UploadId, new[] { (1, p1.ETag), (2, p2.ETag) });
        using var md5 = MD5.Create();
        foreach (var eTag in new[] { p1.ETag, p2.ETag })
        {
            var raw = Convert.FromHexString(eTag);
            md5.TransformBlock(raw, 0, raw.Length, null, 0);
        }
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var expected = Convert.ToHexString(md5.Hash!).ToLowerInvariant() + "-2";
        Assert.Equal(expected, version.ETag);
    }

    [Fact]
    public void CompleteWithMissingPartFails()
    {
        var upload = _store.InitiateMultipart("bucket", "f", "application/octet-stream");
        var p1 = _store.UploadPart(upload.UploadId, 1, new MemoryStream(Bytes("0123456789ABCDE")));
        var ex = Assert.Throws<S3Exception>(() =>
            _store.CompleteMultipart(upload.UploadId, new[] { (1, p1.ETag), (2, "deadbeefdeadbeefdeadbeefdeadbeef") }));
        Assert.Equal("InvalidPart", ex.Code);
    }

    [Fact]
    public void CompleteWithWrongETagFails()
    {
        var upload = _store.InitiateMultipart("bucket", "f", "application/octet-stream");
        _store.UploadPart(upload.UploadId, 1, new MemoryStream(Bytes("0123456789ABCDE")));
        var ex = Assert.Throws<S3Exception>(() =>
            _store.CompleteMultipart(upload.UploadId, new[] { (1, "00000000000000000000000000000000") }));
        Assert.Equal("InvalidPart", ex.Code);
    }

    [Fact]
    public void NonConsecutivePartNumbersFail()
    {
        var upload = _store.InitiateMultipart("bucket", "f", "application/octet-stream");
        var p1 = _store.UploadPart(upload.UploadId, 1, new MemoryStream(Bytes("0123456789ABCDE")));
        var p3 = _store.UploadPart(upload.UploadId, 3, new MemoryStream(Bytes("0123456789ABCDE")));
        var ex = Assert.Throws<S3Exception>(() =>
            _store.CompleteMultipart(upload.UploadId, new[] { (1, p1.ETag), (3, p3.ETag) }));
        Assert.Equal("InvalidPart", ex.Code);
    }

    [Fact]
    public void NonFinalPartBelowMinSizeFails()
    {
        var upload = _store.InitiateMultipart("bucket", "f", "application/octet-stream");
        var small = _store.UploadPart(upload.UploadId, 1, new MemoryStream(Bytes("tiny"))); // 4 < 10
        var big = _store.UploadPart(upload.UploadId, 2, new MemoryStream(Bytes("0123456789ABCDE")));
        var ex = Assert.Throws<S3Exception>(() =>
            _store.CompleteMultipart(upload.UploadId, new[] { (1, small.ETag), (2, big.ETag) }));
        Assert.Equal("InvalidPart", ex.Code);
    }

    [Fact]
    public void AbortDiscardsPartsAndUploadGone()
    {
        var upload = _store.InitiateMultipart("bucket", "f", "application/octet-stream");
        _store.UploadPart(upload.UploadId, 1, new MemoryStream(Bytes("0123456789ABCDE")));
        _store.AbortMultipart(upload.UploadId);
        var ex = Assert.Throws<S3Exception>(() => _store.ListParts(upload.UploadId));
        Assert.Equal("NoSuchUpload", ex.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "blobs"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void UploadPartToMissingUploadFails()
    {
        var ex = Assert.Throws<S3Exception>(() =>
            _store.UploadPart("no-such-upload", 1, new MemoryStream(Bytes("x"))));
        Assert.Equal("NoSuchUpload", ex.Code);
    }

    [Fact]
    public void ListPartsIsSortedByPartNumber()
    {
        var upload = _store.InitiateMultipart("bucket", "f", "application/octet-stream");
        _store.UploadPart(upload.UploadId, 3, new MemoryStream(Bytes("0123456789ABCDE")));
        _store.UploadPart(upload.UploadId, 1, new MemoryStream(Bytes("0123456789ABCDE")));
        var parts = _store.ListParts(upload.UploadId);
        Assert.Equal(new[] { 1, 3 }, parts.Select(p => p.PartNumber).ToList());
    }
}
