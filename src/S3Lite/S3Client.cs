using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace S3Lite;

/// <summary>
/// A minimal S3 client over HttpClient. Every request is signed with SigV4;
/// large uploads go through multipart automatically.
/// </summary>
public sealed class S3Client : IDisposable
{
    public const long MultipartThresholdBytes = 8L * 1024 * 1024;
    public const int MultipartPartBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _accessKey;
    private readonly string _secretKey;

    public S3Client(string endpoint, string accessKey, string secretKey)
    {
        _endpoint = endpoint.TrimEnd('/');
        _accessKey = accessKey;
        _secretKey = secretKey;
        // Loopback endpoints must never go through an egress proxy: a local
        // server is not reachable from the proxy's side of the network, so
        // connect directly with a plain socket.
        var handler = new SocketsHttpHandler();
        var host = new Uri(_endpoint).Host;
        if (host is "localhost" or "127.0.0.1" or "::1"
            || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip)))
        {
            handler.UseProxy = false;
            handler.ConnectCallback = async (context, ct) =>
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var address = addresses.FirstOrDefault(System.Net.IPAddress.IsLoopback) ?? addresses[0];
                var socket = new System.Net.Sockets.Socket(address.AddressFamily,
                    System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            };
        }
        _http = new HttpClient(handler);
    }

    public string Endpoint => _endpoint;

    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------ buckets

    public async Task CreateBucketAsync(string bucket)
    {
        using var response = await SendAsync(HttpMethod.Put, "/" + Uri.EscapeDataString(bucket), Array.Empty<byte>());
        await EnsureSuccessAsync(response);
    }

    public async Task DeleteBucketAsync(string bucket)
    {
        using var response = await SendAsync(HttpMethod.Delete, "/" + Uri.EscapeDataString(bucket), Array.Empty<byte>());
        await EnsureSuccessAsync(response);
    }

    public async Task<IReadOnlyList<(string Name, DateTimeOffset Created)>> ListBucketsAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "/", Array.Empty<byte>());
        await EnsureSuccessAsync(response);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.Descendants().Where(e => e.Name.LocalName == "Bucket")
            .Select(b => (
                b.Elements().First(e => e.Name.LocalName == "Name").Value,
                DateTimeOffset.Parse(b.Elements().First(e => e.Name.LocalName == "CreationDate").Value)))
            .ToList();
    }

    public async Task SetVersioningAsync(string bucket, bool enabled)
    {
        var body = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <VersioningConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Status>{(enabled ? "Enabled" : "Suspended")}</Status>
            </VersioningConfiguration>
            """;
        using var response = await SendAsync(HttpMethod.Put,
            "/" + Uri.EscapeDataString(bucket) + "?versioning", Encoding.UTF8.GetBytes(body));
        await EnsureSuccessAsync(response);
    }

    public async Task<bool> GetVersioningAsync(string bucket)
    {
        using var response = await SendAsync(HttpMethod.Get,
            "/" + Uri.EscapeDataString(bucket) + "?versioning", Array.Empty<byte>());
        await EnsureSuccessAsync(response);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.Descendants().First(e => e.Name.LocalName == "Status").Value == "Enabled";
    }

    public async Task<int> PruneAsync(string bucket, int keep)
    {
        using var response = await SendAsync(HttpMethod.Post,
            "/" + Uri.EscapeDataString(bucket) + $"?prune&keep={keep}", Array.Empty<byte>());
        await EnsureSuccessAsync(response);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        return int.Parse(doc.Descendants().First(e => e.Name.LocalName == "RemovedVersions").Value);
    }

    // ------------------------------------------------------------ objects

    public async Task<(IReadOnlyList<ListedObject> Objects, IReadOnlyList<string> Prefixes, string? NextToken)>
        ListObjectsAsync(string bucket, string? prefix = null, string? delimiter = null,
            int maxKeys = 1000, string? continuationToken = null)
    {
        var query = new List<string> { "list-type=2", $"max-keys={maxKeys}" };
        if (prefix is not null) query.Add("prefix=" + Uri.EscapeDataString(prefix));
        if (delimiter is not null) query.Add("delimiter=" + Uri.EscapeDataString(delimiter));
        if (continuationToken is not null) query.Add("continuation-token=" + Uri.EscapeDataString(continuationToken));
        using var response = await SendAsync(HttpMethod.Get,
            "/" + Uri.EscapeDataString(bucket) + "?" + string.Join("&", query), Array.Empty<byte>());
        await EnsureSuccessAsync(response);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var objects = doc.Descendants().Where(e => e.Name.LocalName == "Contents")
            .Select(c => new ListedObject
            {
                Key = c.Elements().First(e => e.Name.LocalName == "Key").Value,
                ETag = c.Elements().First(e => e.Name.LocalName == "ETag").Value.Trim('"'),
                Size = long.Parse(c.Elements().First(e => e.Name.LocalName == "Size").Value),
                LastModified = DateTimeOffset.Parse(c.Elements().First(e => e.Name.LocalName == "LastModified").Value),
            }).ToList();
        var prefixes = doc.Descendants().Where(e => e.Name.LocalName == "CommonPrefixes")
            .Select(c => c.Elements().First(e => e.Name.LocalName == "Prefix").Value).ToList();
        var next = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "NextContinuationToken")?.Value;
        return (objects, prefixes, next);
    }

    public async Task<IReadOnlyList<ObjectVersion>> ListVersionsAsync(string bucket, string? prefix = null)
    {
        var url = "/" + Uri.EscapeDataString(bucket) + "?versions";
        if (prefix is not null) url += "&prefix=" + Uri.EscapeDataString(prefix);
        using var response = await SendAsync(HttpMethod.Get, url, Array.Empty<byte>());
        await EnsureSuccessAsync(response);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var versions = new List<ObjectVersion>();
        foreach (var el in doc.Root!.Elements())
        {
            if (el.Name.LocalName is not ("Version" or "DeleteMarker")) continue;
            versions.Add(new ObjectVersion
            {
                Key = el.Elements().First(e => e.Name.LocalName == "Key").Value,
                VersionId = el.Elements().First(e => e.Name.LocalName == "VersionId").Value,
                IsDeleteMarker = el.Name.LocalName == "DeleteMarker",
                BlobHash = "",
                Size = el.Elements().FirstOrDefault(e => e.Name.LocalName == "Size") is { } s ? long.Parse(s.Value) : 0,
                ETag = el.Elements().FirstOrDefault(e => e.Name.LocalName == "ETag")?.Value.Trim('"') ?? "",
                ContentType = "",
                LastModified = DateTimeOffset.Parse(el.Elements().First(e => e.Name.LocalName == "LastModified").Value),
                IsLatest = el.Elements().First(e => e.Name.LocalName == "IsLatest").Value == "true",
            });
        }
        return versions;
    }

    /// <summary>Uploads a file; uses multipart automatically above the threshold.</summary>
    public async Task<ObjectVersion> PutObjectAsync(string bucket, string key, string filePath,
        string? contentType = null, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var file = new FileInfo(filePath);
        var ct = contentType ?? GuessContentType(filePath);
        if (file.Length > MultipartThresholdBytes)
            return await PutMultipartAsync(bucket, key, filePath, ct, metadata);

        var bytes = await File.ReadAllBytesAsync(filePath);
        using var response = await SendAsync(HttpMethod.Put, ObjectUrl(bucket, key), bytes,
            contentType: ct, metadata: metadata);
        await EnsureSuccessAsync(response);
        return new ObjectVersion
        {
            Key = key,
            VersionId = Header(response, "x-amz-version-id"),
            IsDeleteMarker = false,
            BlobHash = "",
            Size = bytes.LongLength,
            ETag = Header(response, "ETag").Trim('"'),
            ContentType = ct,
            LastModified = DateTimeOffset.UtcNow,
            IsLatest = true,
        };
    }

    public async Task<Stream> GetObjectAsync(string bucket, string key, string? versionId = null)
    {
        var url = ObjectUrl(bucket, key);
        if (versionId is not null) url += "?versionId=" + Uri.EscapeDataString(versionId);
        var response = await SendAsync(HttpMethod.Get, url, Array.Empty<byte>());
        await EnsureSuccessAsync(response);
        return await response.Content.ReadAsStreamAsync();
    }

    public async Task<ObjectVersion?> HeadObjectAsync(string bucket, string key, string? versionId = null)
    {
        var url = ObjectUrl(bucket, key);
        if (versionId is not null) url += "?versionId=" + Uri.EscapeDataString(versionId);
        using var response = await SendAsync(HttpMethod.Head, url, Array.Empty<byte>());
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response);
        return new ObjectVersion
        {
            Key = key,
            VersionId = Header(response, "x-amz-version-id"),
            IsDeleteMarker = false,
            BlobHash = "",
            Size = response.Content.Headers.ContentLength ?? 0,
            ETag = Header(response, "ETag").Trim('"'),
            ContentType = response.Content.Headers.ContentType?.MediaType ?? "",
            LastModified = response.Content.Headers.LastModified ?? DateTimeOffset.UtcNow,
            IsLatest = true,
        };
    }

    public async Task DeleteObjectAsync(string bucket, string key, string? versionId = null)
    {
        var url = ObjectUrl(bucket, key);
        if (versionId is not null) url += "?versionId=" + Uri.EscapeDataString(versionId);
        using var response = await SendAsync(HttpMethod.Delete, url, Array.Empty<byte>());
        await EnsureSuccessAsync(response);
    }

    public async Task CopyObjectAsync(string srcBucket, string srcKey, string dstBucket, string dstKey)
    {
        var source = "/" + Uri.EscapeDataString(srcBucket) + "/" + KeyPath(srcKey);
        var extra = new List<(string, string)> { ("x-amz-copy-source", source) };
        using var request = new HttpRequestMessage(HttpMethod.Put, _endpoint + ObjectUrl(dstBucket, dstKey));
        request.Headers.TryAddWithoutValidation("x-amz-copy-source", source);
        SigV4.SignRequest(request, _accessKey, _secretKey, Array.Empty<byte>(), extraHeaders: extra);
        using var response = await _http.SendAsync(request);
        await EnsureSuccessAsync(response);
    }

    public string Presign(string bucket, string key, int expiresSeconds) =>
        SigV4.PresignUrl(_endpoint, _accessKey, _secretKey, "GET", ObjectUrl(bucket, key), expiresSeconds);

    // ------------------------------------------------------------ multipart (client)

    private async Task<ObjectVersion> PutMultipartAsync(string bucket, string key, string filePath,
        string contentType, IReadOnlyDictionary<string, string>? metadata)
    {
        var uploadId = await InitiateMultipartAsync(bucket, key, contentType, metadata);
        var parts = new List<(int, string)>();
        try
        {
            using var fs = File.OpenRead(filePath);
            var buffer = new byte[MultipartPartBytes];
            var partNumber = 1;
            int read;
            while ((read = await fs.ReadAsync(buffer)) > 0)
            {
                var chunk = buffer.AsSpan(0, read).ToArray();
                var eTag = await UploadPartAsync(bucket, key, uploadId, partNumber, chunk);
                parts.Add((partNumber, eTag));
                partNumber++;
            }
            return await CompleteMultipartAsync(bucket, key, uploadId, parts);
        }
        catch
        {
            await AbortMultipartAsync(bucket, key, uploadId);
            throw;
        }
    }

    public async Task<string> InitiateMultipartAsync(string bucket, string key, string contentType,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        using var response = await SendAsync(HttpMethod.Post, ObjectUrl(bucket, key) + "?uploads",
            Array.Empty<byte>(), contentType: contentType, metadata: metadata);
        await EnsureSuccessAsync(response);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.Descendants().First(e => e.Name.LocalName == "UploadId").Value;
    }

    public async Task<string> UploadPartAsync(string bucket, string key, string uploadId,
        int partNumber, byte[] bytes)
    {
        using var response = await SendAsync(HttpMethod.Put,
            ObjectUrl(bucket, key) + $"?partNumber={partNumber}&uploadId={Uri.EscapeDataString(uploadId)}", bytes);
        await EnsureSuccessAsync(response);
        return Header(response, "ETag").Trim('"');
    }

    public async Task<ObjectVersion> CompleteMultipartAsync(string bucket, string key, string uploadId,
        IReadOnlyList<(int PartNumber, string ETag)> parts)
    {
        var sb = new StringBuilder();
        sb.Append("<CompleteMultipartUpload>");
        foreach (var (number, eTag) in parts)
            sb.Append($"<Part><PartNumber>{number}</PartNumber><ETag>\"{eTag}\"</ETag></Part>");
        sb.Append("</CompleteMultipartUpload>");
        using var response = await SendAsync(HttpMethod.Post,
            ObjectUrl(bucket, key) + "?uploadId=" + Uri.EscapeDataString(uploadId),
            Encoding.UTF8.GetBytes(sb.ToString()));
        await EnsureSuccessAsync(response);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        return new ObjectVersion
        {
            Key = key,
            VersionId = "",
            IsDeleteMarker = false,
            BlobHash = "",
            Size = 0,
            ETag = doc.Descendants().First(e => e.Name.LocalName == "ETag").Value.Trim('"'),
            ContentType = "",
            LastModified = DateTimeOffset.UtcNow,
            IsLatest = true,
        };
    }

    public async Task AbortMultipartAsync(string bucket, string key, string uploadId)
    {
        using var response = await SendAsync(HttpMethod.Delete,
            ObjectUrl(bucket, key) + "?uploadId=" + Uri.EscapeDataString(uploadId), Array.Empty<byte>());
        await EnsureSuccessAsync(response);
    }

    // ------------------------------------------------------------ plumbing

    private string ObjectUrl(string bucket, string key) =>
        "/" + Uri.EscapeDataString(bucket) + "/" + KeyPath(key);

    private static string KeyPath(string key) =>
        string.Join("/", key.Split('/').Select(Uri.EscapeDataString));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[] payload,
        string? contentType = null, IReadOnlyDictionary<string, string>? metadata = null)
    {
        using var request = new HttpRequestMessage(method, _endpoint + path);
        var extra = new List<(string, string)>();
        if (contentType is not null)
        {
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            extra.Add(("content-type", contentType));
        }
        else if (payload.Length > 0)
        {
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            extra.Add(("content-type", "application/octet-stream"));
        }
        if (metadata is not null)
        {
            foreach (var (name, value) in metadata)
            {
                request.Headers.TryAddWithoutValidation("x-amz-meta-" + name, value);
                extra.Add(("x-amz-meta-" + name.ToLowerInvariant(), value));
            }
        }
        SigV4.SignRequest(request, _accessKey, _secretKey, payload, extraHeaders: extra);
        return await _http.SendAsync(request);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? "" : "";

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            var doc = XDocument.Parse(body);
            var code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value ?? "Unknown";
            var message = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value ?? body;
            throw new S3Exception(code, (int)response.StatusCode, message);
        }
        catch (S3Exception) { throw; }
        catch
        {
            throw new S3Exception("Unknown", (int)response.StatusCode, body);
        }
    }

    private static string GuessContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".gz" => "application/gzip",
            ".md" => "text/markdown",
            _ => "application/octet-stream",
        };
}
