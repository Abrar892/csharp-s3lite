using System.Text;
using System.Xml.Linq;

namespace S3Lite.Tests;

public sealed class S3XmlTests
{
    [Fact]
    public void ListBucketsXmlIsWellFormedAndContainsNames()
    {
        var xml = S3Xml.ListBuckets(new[]
        {
            new BucketManifest { Name = "alpha", Created = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero), VersioningEnabled = false },
            new BucketManifest { Name = "beta", Created = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero), VersioningEnabled = true },
        });
        var doc = XDocument.Parse(xml);
        var names = doc.Descendants().Where(e => e.Name.LocalName == "Name").Select(e => e.Value).ToList();
        Assert.Equal(new[] { "alpha", "beta" }, names);
    }

    [Fact]
    public void ErrorXmlContainsCodeAndMessage()
    {
        var xml = S3Xml.Error("NoSuchBucket", "The specified bucket does not exist: x", "/x", "req-1");
        var doc = XDocument.Parse(xml);
        Assert.Equal("NoSuchBucket", doc.Descendants().First(e => e.Name.LocalName == "Code").Value);
        Assert.Equal("req-1", doc.Descendants().First(e => e.Name.LocalName == "RequestId").Value);
    }

    [Fact]
    public void ParseCompleteMultipartRoundtrip()
    {
        var body = Encoding.UTF8.GetBytes(
            "<CompleteMultipartUpload><Part><PartNumber>2</PartNumber><ETag>\"abc123\"</ETag></Part>" +
            "<Part><PartNumber>1</PartNumber><ETag>\"def456\"</ETag></Part></CompleteMultipartUpload>");
        var parts = S3Xml.ParseCompleteMultipart(body);
        Assert.Equal(new[] { (2, "abc123"), (1, "def456") }, parts);
    }

    [Fact]
    public void ParseVersioningRoundtrip()
    {
        Assert.True(S3Xml.ParseVersioning(Encoding.UTF8.GetBytes(
            "<VersioningConfiguration><Status>Enabled</Status></VersioningConfiguration>")));
        Assert.False(S3Xml.ParseVersioning(Encoding.UTF8.GetBytes(
            "<VersioningConfiguration><Status>Suspended</Status></VersioningConfiguration>")));
        Assert.Throws<S3Exception>(() => S3Xml.ParseVersioning(Encoding.UTF8.GetBytes(
            "<VersioningConfiguration><Status>Weird</Status></VersioningConfiguration>")));
    }

    [Fact]
    public void ListObjectsV2XmlFlagsTruncation()
    {
        var xml = S3Xml.ListObjectsV2("b", "pre", null, 2,
            new[] { new ListedObject { Key = "pre/a", ETag = "e1", Size = 3, LastModified = DateTimeOffset.UtcNow } },
            Array.Empty<string>(), "pre/a", isTruncated: true);
        var doc = XDocument.Parse(xml);
        Assert.Equal("true", doc.Descendants().First(e => e.Name.LocalName == "IsTruncated").Value);
        Assert.Equal("pre/a", doc.Descendants().First(e => e.Name.LocalName == "NextContinuationToken").Value);
    }
}
