using System.Xml.Linq;

namespace S3Lite;

/// <summary>S3-compatible XML responses and request-body parsing (XDocument, no dependencies).</summary>
public static class S3Xml
{
    private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    public static string Error(string code, string message, string resource, string requestId)
    {
        var doc = new XDocument(new XElement("Error",
            new XElement("Code", code),
            new XElement("Message", message),
            new XElement("Resource", resource),
            new XElement("RequestId", requestId)));
        return doc.ToString();
    }

    public static string ListBuckets(IReadOnlyList<BucketManifest> buckets)
    {
        var doc = new XDocument(new XElement(Ns + "ListAllMyBucketsResult",
            new XElement(Ns + "Owner", new XElement(Ns + "ID", "s3lite"), new XElement(Ns + "DisplayName", "s3lite")),
            new XElement(Ns + "Buckets", buckets.Select(b =>
                new XElement(Ns + "Bucket",
                    new XElement(Ns + "Name", b.Name),
                    new XElement(Ns + "CreationDate", Iso(b.Created)))))));
        return doc.ToString();
    }

    public static string ListObjectsV2(string bucket, string? prefix, string? delimiter, int maxKeys,
        IReadOnlyList<ListedObject> objects, IReadOnlyList<string> commonPrefixes,
        string? nextToken, bool isTruncated)
    {
        var root = new XElement(Ns + "ListBucketResult",
            new XElement(Ns + "Name", bucket),
            new XElement(Ns + "Prefix", prefix ?? ""),
            new XElement(Ns + "KeyCount", objects.Count + commonPrefixes.Count),
            new XElement(Ns + "MaxKeys", maxKeys),
            new XElement(Ns + "IsTruncated", isTruncated ? "true" : "false"));
        if (delimiter is not null) root.Add(new XElement(Ns + "Delimiter", delimiter));
        if (nextToken is not null) root.Add(new XElement(Ns + "NextContinuationToken", nextToken));
        foreach (var o in objects)
        {
            root.Add(new XElement(Ns + "Contents",
                new XElement(Ns + "Key", o.Key),
                new XElement(Ns + "LastModified", Iso(o.LastModified)),
                new XElement(Ns + "ETag", "\"" + o.ETag + "\""),
                new XElement(Ns + "Size", o.Size),
                new XElement(Ns + "StorageClass", "STANDARD")));
        }
        foreach (var p in commonPrefixes)
            root.Add(new XElement(Ns + "CommonPrefixes", new XElement(Ns + "Prefix", p)));
        return new XDocument(root).ToString();
    }

    public static string ListVersions(string bucket, IReadOnlyList<ObjectVersion> versions)
    {
        var root = new XElement(Ns + "ListVersionsResult",
            new XElement(Ns + "Name", bucket),
            new XElement(Ns + "IsTruncated", "false"),
            new XElement(Ns + "MaxKeys", versions.Count));
        foreach (var v in versions)
        {
            var element = new XElement(Ns + (v.IsDeleteMarker ? "DeleteMarker" : "Version"),
                new XElement(Ns + "Key", v.Key),
                new XElement(Ns + "VersionId", v.VersionId),
                new XElement(Ns + "IsLatest", v.IsLatest ? "true" : "false"),
                new XElement(Ns + "LastModified", Iso(v.LastModified)));
            if (!v.IsDeleteMarker)
            {
                element.Add(
                    new XElement(Ns + "ETag", "\"" + v.ETag + "\""),
                    new XElement(Ns + "Size", v.Size),
                    new XElement(Ns + "StorageClass", "STANDARD"));
            }
            root.Add(element);
        }
        return new XDocument(root).ToString();
    }

    public static string Versioning(bool enabled) =>
        new XDocument(new XElement(Ns + "VersioningConfiguration",
            new XElement(Ns + "Status", enabled ? "Enabled" : "Suspended"))).ToString();

    public static string InitiateMultipart(string bucket, string key, string uploadId) =>
        new XDocument(new XElement(Ns + "InitiateMultipartUploadResult",
            new XElement(Ns + "Bucket", bucket),
            new XElement(Ns + "Key", key),
            new XElement(Ns + "UploadId", uploadId))).ToString();

    public static string ListParts(string bucket, string key, string uploadId, IReadOnlyList<MultipartPart> parts)
    {
        var root = new XElement(Ns + "ListPartsResult",
            new XElement(Ns + "Bucket", bucket),
            new XElement(Ns + "Key", key),
            new XElement(Ns + "UploadId", uploadId),
            new XElement(Ns + "IsTruncated", "false"),
            new XElement(Ns + "MaxParts", parts.Count));
        foreach (var p in parts)
        {
            root.Add(new XElement(Ns + "Part",
                new XElement(Ns + "PartNumber", p.PartNumber),
                new XElement(Ns + "ETag", "\"" + p.ETag + "\""),
                new XElement(Ns + "Size", p.Size)));
        }
        return new XDocument(root).ToString();
    }

    public static string CompleteMultipart(string bucket, string key, string eTag, string location) =>
        new XDocument(new XElement(Ns + "CompleteMultipartUploadResult",
            new XElement(Ns + "Location", location),
            new XElement(Ns + "Bucket", bucket),
            new XElement(Ns + "Key", key),
            new XElement(Ns + "ETag", "\"" + eTag + "\""))).ToString();

    public static string CopyObjectResult(string eTag, DateTimeOffset lastModified) =>
        new XDocument(new XElement(Ns + "CopyObjectResult",
            new XElement(Ns + "ETag", "\"" + eTag + "\""),
            new XElement(Ns + "LastModified", Iso(lastModified)))).ToString();

    /// <summary>S3Lite extension: result of POST /{bucket}?prune&amp;keep=N.</summary>
    public static string PruneResult(string bucket, int removed) =>
        new XDocument(new XElement(Ns + "PruneResult",
            new XElement(Ns + "Bucket", bucket),
            new XElement(Ns + "RemovedVersions", removed))).ToString();

    // ------------------------------------------------------------ request parsing

    public static List<(int PartNumber, string ETag)> ParseCompleteMultipart(byte[] body)
    {
        var doc = XDocument.Parse(AsText(body));
        var parts = new List<(int, string)>();
        foreach (var part in doc.Descendants().Where(e => e.Name.LocalName == "Part"))
        {
            var number = part.Elements().FirstOrDefault(e => e.Name.LocalName == "PartNumber")?.Value
                ?? throw S3Exception.InvalidArgument("CompleteMultipartUpload: Part is missing PartNumber.");
            var eTag = part.Elements().FirstOrDefault(e => e.Name.LocalName == "ETag")?.Value
                ?? throw S3Exception.InvalidArgument("CompleteMultipartUpload: Part is missing ETag.");
            parts.Add((int.Parse(number), eTag.Trim('"')));
        }
        return parts;
    }

    public static bool ParseVersioning(byte[] body)
    {
        var doc = XDocument.Parse(AsText(body));
        var status = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Status")?.Value
            ?? throw S3Exception.InvalidArgument("VersioningConfiguration is missing Status.");
        return status switch
        {
            "Enabled" => true,
            "Suspended" => false,
            _ => throw S3Exception.InvalidArgument($"Invalid versioning status: {status}"),
        };
    }

    private static string AsText(byte[] body) =>
        System.Text.Encoding.UTF8.GetString(body);
}
