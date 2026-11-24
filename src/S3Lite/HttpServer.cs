using System.Net;
using System.Text;
using System.Xml.Linq;

namespace S3Lite;

/// <summary>
/// The HTTP front end: an HttpListener server that routes S3 path-style
/// requests, verifies SigV4 auth, and delegates storage to ObjectStore.
/// </summary>
public sealed class HttpServer
{
    private readonly ObjectStore _store;
    private readonly List<ApiKey> _keys;
    private readonly int _port;

    public HttpServer(ObjectStore store, List<ApiKey> keys, int port)
    {
        _store = store;
        _keys = keys;
        _port = port;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        listener.Start();
        Console.WriteLine($"S3Lite listening on http://127.0.0.1:{_port}/ (data: {_store.DataDir})");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var requestId = Guid.NewGuid().ToString("N")[..16];
        try
        {
            var body = await ReadBodyAsync(ctx.Request);
            var headers = HeaderMap(ctx.Request);
            var method = ctx.Request.HttpMethod;
            var path = ctx.Request.Url!.AbsolutePath;
            var query = SigV4.ParseQuery(ctx.Request.Url.Query);

            var identity = SigV4.VerifyRequest(method, path, ctx.Request.Url.Query, headers, body,
                accessKey => _keys.FirstOrDefault(k => k.AccessKey == accessKey)?.SecretKey);
            var key = _keys.First(k => k.AccessKey == identity.AccessKey);

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString).ToArray();
            await RouteAsync(ctx, requestId, key, method, segments, query, headers, body);
        }
        catch (S3Exception ex)
        {
            await WriteErrorAsync(ctx, requestId, ex);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(ctx, requestId,
                new S3Exception("InternalError", 500, $"The server encountered an unexpected error: {ex.Message}"));
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* already closed */ }
        }
    }

    // ------------------------------------------------------------ routing

    private async Task RouteAsync(HttpListenerContext ctx, string requestId, ApiKey key,
        string method, string[] segments, List<(string Name, string Value)> query,
        Dictionary<string, string> headers, byte[] body)
    {
        string? Query(string name) => query.FirstOrDefault(p => p.Name == name).Name is null
            ? null : query.First(p => p.Name == name).Value;
        bool HasQuery(string name) => query.Any(p => p.Name == name);

        if (segments.Length == 0)
        {
            Require(key, "read");
            if (method != "GET") throw new S3Exception("MethodNotAllowed", 405, $"Method {method} is not allowed on /.");
            var buckets = _store.ListBuckets();
            await WriteXmlAsync(ctx, 200, S3Xml.ListBuckets(buckets), requestId);
            return;
        }

        var bucket = segments[0];
        var objectKey = segments.Length > 1 ? string.Join("/", segments[1..]) : null;

        if (objectKey is null)
        {
            // Bucket-level operations.
            switch (method)
            {
                case "PUT" when HasQuery("versioning"):
                    Require(key, "write");
                    _store.SetVersioning(bucket, S3Xml.ParseVersioning(body));
                    await WriteEmptyAsync(ctx, 200, requestId);
                    return;
                case "GET" when HasQuery("versioning"):
                    Require(key, "read");
                    await WriteXmlAsync(ctx, 200, S3Xml.Versioning(_store.GetBucket(bucket).VersioningEnabled), requestId);
                    return;
                case "GET" when Query("list-type") == "2":
                    Require(key, "read");
                    await ListObjectsAsync(ctx, requestId, bucket, query);
                    return;
                case "GET" when HasQuery("versions"):
                    Require(key, "read");
                    var versions = _store.ListVersions(bucket, Query("prefix"));
                    await WriteXmlAsync(ctx, 200, S3Xml.ListVersions(bucket, versions), requestId);
                    return;
                case "POST" when HasQuery("prune"):
                    Require(key, "admin");
                    var keep = int.TryParse(Query("keep"), out var k) ? k : 1;
                    var removed = _store.PruneVersions(bucket, keep);
                    await WriteXmlAsync(ctx, 200, S3Xml.PruneResult(bucket, removed), requestId);
                    return;
                case "PUT":
                    Require(key, "write");
                    _store.CreateBucket(bucket);
                    ctx.Response.Headers["Location"] = "/" + bucket;
                    await WriteEmptyAsync(ctx, 200, requestId);
                    return;
                case "DELETE":
                    Require(key, "write");
                    _store.DeleteBucket(bucket);
                    await WriteEmptyAsync(ctx, 204, requestId);
                    return;
                default:
                    throw new S3Exception("MethodNotAllowed", 405, $"Method {method} is not allowed on a bucket.");
            }
        }

        // Object-level operations.
        var uploadId = Query("uploadId");
        switch (method)
        {
            case "PUT" when HasQuery("partNumber") && uploadId is not null:
                Require(key, "write");
                var partNumber = int.Parse(Query("partNumber")!);
                var part = _store.UploadPart(uploadId, partNumber, new MemoryStream(body));
                ctx.Response.Headers["ETag"] = "\"" + part.ETag + "\"";
                await WriteEmptyAsync(ctx, 200, requestId);
                return;
            case "PUT" when headers.TryGetValue("x-amz-copy-source", out var copySource):
                Require(key, "write");
                await CopyObjectAsync(ctx, requestId, bucket, objectKey, copySource, headers);
                return;
            case "PUT":
                Require(key, "write");
                var version = _store.PutObject(bucket, objectKey, new MemoryStream(body),
                    ContentType(headers), UserMetadata(headers));
                ctx.Response.Headers["ETag"] = "\"" + version.ETag + "\"";
                ctx.Response.Headers["x-amz-version-id"] = version.VersionId;
                await WriteEmptyAsync(ctx, 200, requestId);
                return;
            case "GET" when HasQuery("uploads"):
                Require(key, "read");
                var parts = _store.ListParts(uploadId!);
                await WriteXmlAsync(ctx, 200, S3Xml.ListParts(bucket, objectKey, uploadId!, parts), requestId);
                return;
            case "GET":
                Require(key, "read");
                await GetObjectAsync(ctx, requestId, bucket, objectKey, Query("versionId"), headers);
                return;
            case "HEAD":
                Require(key, "read");
                await HeadObjectAsync(ctx, requestId, bucket, objectKey, Query("versionId"));
                return;
            case "DELETE" when uploadId is not null:
                Require(key, "write");
                _store.AbortMultipart(uploadId);
                await WriteEmptyAsync(ctx, 204, requestId);
                return;
            case "DELETE":
                Require(key, "write");
                _store.DeleteObject(bucket, objectKey, Query("versionId"));
                await WriteEmptyAsync(ctx, 204, requestId);
                return;
            case "POST" when HasQuery("uploads"):
                Require(key, "write");
                var upload = _store.InitiateMultipart(bucket, objectKey, ContentType(headers), UserMetadata(headers));
                await WriteXmlAsync(ctx, 200, S3Xml.InitiateMultipart(bucket, objectKey, upload.UploadId), requestId);
                return;
            case "POST" when uploadId is not null:
                Require(key, "write");
                var completedParts = S3Xml.ParseCompleteMultipart(body);
                var completed = _store.CompleteMultipart(uploadId, completedParts);
                var location = $"/{Uri.EscapeDataString(bucket)}/{string.Join("/", objectKey.Split('/').Select(Uri.EscapeDataString))}";
                await WriteXmlAsync(ctx, 200,
                    S3Xml.CompleteMultipart(bucket, objectKey, completed.ETag, location), requestId);
                return;
            default:
                throw new S3Exception("MethodNotAllowed", 405, $"Method {method} is not allowed on an object.");
        }
    }

    // ------------------------------------------------------------ handlers

    private async Task ListObjectsAsync(HttpListenerContext ctx, string requestId, string bucket,
        List<(string Name, string Value)> query)
    {
        string? Get(string name) => query.FirstOrDefault(p => p.Name == name).Name is null
            ? null : query.First(p => p.Name == name).Value;
        var maxKeys = int.TryParse(Get("max-keys"), out var m) ? m : 1000;
        var (objects, prefixes, next) = _store.ListObjects(bucket, Get("prefix"), Get("delimiter"),
            maxKeys, Get("continuation-token"));
        var isTruncated = next is not null;
        await WriteXmlAsync(ctx, 200,
            S3Xml.ListObjectsV2(bucket, Get("prefix"), Get("delimiter"), maxKeys,
                objects, prefixes, next, isTruncated), requestId);
    }

    private async Task GetObjectAsync(HttpListenerContext ctx, string requestId, string bucket,
        string key, string? versionId, Dictionary<string, string> headers)
    {
        var (content, version) = _store.GetObject(bucket, key, versionId);
        await using var _ = content;
        SetObjectHeaders(ctx, version);
        ctx.Response.Headers["x-amz-request-id"] = requestId;

        var range = headers.TryGetValue("Range", out var rangeHeader) ? ParseRange(rangeHeader, version.Size) : null;
        if (range is null && rangeHeader is not null)
            throw new S3Exception("InvalidRange", 416, $"The requested range is not satisfiable: {rangeHeader}");

        if (range is not null)
        {
            ctx.Response.StatusCode = 206;
            ctx.Response.Headers["Content-Range"] = $"bytes {range.Value.Start}-{range.Value.End}/{version.Size}";
            ctx.Response.ContentLength64 = range.Value.End - range.Value.Start + 1;
            content.Seek(range.Value.Start, SeekOrigin.Begin);
            await CopyBoundedAsync(content, ctx.Response.OutputStream, range.Value.End - range.Value.Start + 1);
        }
        else
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = version.Size;
            await content.CopyToAsync(ctx.Response.OutputStream);
        }
    }

    private async Task HeadObjectAsync(HttpListenerContext ctx, string requestId, string bucket,
        string key, string? versionId)
    {
        var version = _store.HeadObject(bucket, key, versionId);
        SetObjectHeaders(ctx, version);
        ctx.Response.Headers["x-amz-request-id"] = requestId;
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength64 = version.Size;
        await Task.CompletedTask;
    }

    private async Task CopyObjectAsync(HttpListenerContext ctx, string requestId, string bucket,
        string key, string copySource, Dictionary<string, string> headers)
    {
        // x-amz-copy-source: /source-bucket/source-key (URL-encoded).
        var source = Uri.UnescapeDataString(copySource.TrimStart('/'));
        var slash = source.IndexOf('/');
        if (slash < 0) throw S3Exception.InvalidArgument("x-amz-copy-source must be /bucket/key.");
        var srcBucket = source[..slash];
        var srcKey = source[(slash + 1)..];
        var srcVersionId = headers.TryGetValue("x-amz-copy-source-version-id", out var v) ? v : null;
        var copied = _store.CopyObject(srcBucket, srcKey, bucket, key, srcVersionId,
            ContentType(headers), UserMetadata(headers).Count > 0 ? UserMetadata(headers) : null);
        await WriteXmlAsync(ctx, 200, S3Xml.CopyObjectResult(copied.ETag, copied.LastModified), requestId);
    }

    // ------------------------------------------------------------ helpers

    private static void SetObjectHeaders(HttpListenerContext ctx, ObjectVersion version)
    {
        ctx.Response.ContentType = version.ContentType;
        ctx.Response.Headers["ETag"] = "\"" + version.ETag + "\"";
        ctx.Response.Headers["x-amz-version-id"] = version.VersionId;
        ctx.Response.Headers["Last-Modified"] = version.LastModified.ToUniversalTime().ToString("R");
        foreach (var (name, value) in version.UserMetadata)
            ctx.Response.Headers["x-amz-meta-" + name] = value;
    }

    private static (long Start, long End)? ParseRange(string header, long size)
    {
        // bytes=start-end | bytes=start- | bytes=-suffix
        if (!header.StartsWith("bytes=", StringComparison.Ordinal)) return null;
        var spec = header["bytes=".Length..];
        var dash = spec.IndexOf('-');
        if (dash < 0) return null;
        var startPart = spec[..dash];
        var endPart = spec[(dash + 1)..];
        if (startPart.Length == 0 && endPart.Length > 0)
        {
            var suffix = long.Parse(endPart);
            if (suffix <= 0) return null;
            return (Math.Max(0, size - suffix), size - 1);
        }
        if (!long.TryParse(startPart, out var start) || start < 0 || start >= size) return null;
        var end = endPart.Length > 0 ? long.Parse(endPart) : size - 1;
        if (end >= size) end = size - 1;
        if (end < start) return null;
        return (start, end);
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long bytes)
    {
        var buffer = new byte[64 * 1024];
        var remaining = bytes;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) break;
            await destination.WriteAsync(buffer, 0, read);
            remaining -= read;
        }
    }

    private static string ContentType(Dictionary<string, string> headers) =>
        headers.TryGetValue("content-type", out var ct) && !string.IsNullOrWhiteSpace(ct)
            ? ct.Split(';')[0].Trim()
            : "application/octet-stream";

    private static Dictionary<string, string> UserMetadata(Dictionary<string, string> headers)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (name.StartsWith("x-amz-meta-", StringComparison.Ordinal))
                meta[name["x-amz-meta-".Length..]] = value;
        }
        return meta;
    }

    private static Dictionary<string, string> HeaderMap(HttpListenerRequest request)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? name in request.Headers.AllKeys)
        {
            if (name is not null) map[name] = request.Headers[name] ?? "";
        }
        if (!map.ContainsKey("host"))
            map["host"] = request.Url!.Host + (request.Url.IsDefaultPort ? "" : ":" + request.Url.Port);
        if (!map.ContainsKey("content-type") && !string.IsNullOrEmpty(request.ContentType))
            map["content-type"] = request.ContentType;
        return map;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 <= 0) return Array.Empty<byte>();
        using var ms = new MemoryStream();
        await request.InputStream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static void Require(ApiKey key, string permission)
    {
        if (!key.Can(permission))
            throw S3Exception.AccessDenied($"Key {key.AccessKey} lacks the '{permission}' permission.");
    }

    private static async Task WriteXmlAsync(HttpListenerContext ctx, int status, string xml, string requestId)
    {
        var bytes = new UTF8Encoding(false).GetBytes(xml);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/xml";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.Headers["x-amz-request-id"] = requestId;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task WriteEmptyAsync(HttpListenerContext ctx, int status, string requestId)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentLength64 = 0;
        ctx.Response.Headers["x-amz-request-id"] = requestId;
        await Task.CompletedTask;
    }

    private static async Task WriteErrorAsync(HttpListenerContext ctx, string requestId, S3Exception ex)
    {
        try
        {
            var resource = ctx.Request.Url?.AbsolutePath ?? "/";
            var xml = S3Xml.Error(ex.Code, ex.Message, resource, requestId);
            var bytes = new UTF8Encoding(false).GetBytes(xml);
            ctx.Response.StatusCode = ex.HttpStatus;
            ctx.Response.ContentType = "application/xml";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.Headers["x-amz-request-id"] = requestId;
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
        catch { /* response already gone */ }
    }
}
