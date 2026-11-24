using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace S3Lite.Tests;

/// <summary>End-to-end tests against a real HttpListener server on localhost.</summary>
public sealed class HttpServerTests : IDisposable
{
    private const string AccessKey = "AKIDTEST";
    private const string SecretKey = "test-secret-key-for-http-tests";
    private readonly string _dir;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly S3Client _client;

    public HttpServerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "s3lite-http-" + Guid.NewGuid().ToString("N"));
        _port = FreePort();
        var store = new ObjectStore(_dir);
        var keys = new List<ApiKey> { new(AccessKey, SecretKey, new[] { "admin" }) };
        var server = new HttpServer(store, keys, _port);
        _ = server.StartAsync(_cts.Token);
        WaitForServer();
        _client = new S3Client($"http://127.0.0.1:{_port}", AccessKey, SecretKey);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void WaitForServer()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Connect("127.0.0.1", _port);
                return;
            }
            catch (SocketException)
            {
                Thread.Sleep(50);
            }
        }
        throw new TimeoutException("Test server did not start in time.");
    }

    /// <summary>An HttpClient that connects directly to loopback (see S3Client for why).</summary>
    private static HttpClient LoopbackClient()
    {
        var handler = new SocketsHttpHandler { UseProxy = false };
        handler.ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var address = addresses.FirstOrDefault(IPAddress.IsLoopback) ?? addresses[0];
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        };
        return new HttpClient(handler);
    }

    [Fact]
    public async Task EndToEndBucketObjectFlow()
    {
        await _client.CreateBucketAsync("photos");
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        await File.WriteAllTextAsync(tmp, "hello via http");
        try
        {
            await _client.PutObjectAsync("photos", "hello.txt", tmp);
            var objects = await _client.ListObjectsAsync("photos");
            Assert.Single(objects.Objects);
            Assert.Equal("hello.txt", objects.Objects[0].Key);
            await using var stream = await _client.GetObjectAsync("photos", "hello.txt");
            Assert.Equal("hello via http", await new StreamReader(stream).ReadToEndAsync());
            await _client.DeleteObjectAsync("photos", "hello.txt");
            Assert.Empty((await _client.ListObjectsAsync("photos")).Objects);
            await _client.DeleteBucketAsync("photos");
            Assert.Empty(await _client.ListBucketsAsync());
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task PresignedUrlWorks()
    {
        await _client.CreateBucketAsync("pub");
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        await File.WriteAllTextAsync(tmp, "public content");
        try
        {
            await _client.PutObjectAsync("pub", "doc.txt", tmp);
            var url = _client.Presign("pub", "doc.txt", 3600);
            using var http = LoopbackClient();
            var body = await http.GetStringAsync(url);
            Assert.Equal("public content", body);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task RangeGetReturnsPartialContent()
    {
        await _client.CreateBucketAsync("range");
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        await File.WriteAllTextAsync(tmp, "0123456789");
        try
        {
            await _client.PutObjectAsync("range", "digits.txt", tmp);
            using var http = LoopbackClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_port}/range/digits.txt");
            request.Headers.TryAddWithoutValidation("Range", "bytes=3-5");
            SigV4.SignRequest(request, AccessKey, SecretKey, Array.Empty<byte>(),
                extraHeaders: new[] { ("Range", "bytes=3-5") });
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("bytes 3-5/10", response.Content.Headers.ContentRange?.ToString());
            Assert.Equal("345", await response.Content.ReadAsStringAsync());
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task UnsignedRequestRejected()
    {
        using var http = LoopbackClient();
        using var response = await http.GetAsync($"http://127.0.0.1:{_port}/");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("AccessDenied", body);
    }

    [Fact]
    public async Task WrongSecretRejected()
    {
        using var badClient = new S3Client($"http://127.0.0.1:{_port}", AccessKey, "wrong-secret");
        var ex = await Assert.ThrowsAsync<S3Exception>(() => badClient.ListBucketsAsync());
        Assert.Equal("SignatureDoesNotMatch", ex.Code);
    }

    [Fact]
    public async Task VersioningViaHttp()
    {
        await _client.CreateBucketAsync("vers");
        await _client.SetVersioningAsync("vers", true);
        Assert.True(await _client.GetVersioningAsync("vers"));
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(tmp, "one");
            await _client.PutObjectAsync("vers", "k", tmp);
            await File.WriteAllTextAsync(tmp, "two");
            await _client.PutObjectAsync("vers", "k", tmp);
            var versions = await _client.ListVersionsAsync("vers");
            Assert.Equal(2, versions.Count);
            Assert.True(versions[0].IsLatest);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task MultipartUploadViaHttp()
    {
        await _client.CreateBucketAsync("big");
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin");
        var bytes = new byte[9 * 1024 * 1024];
        RandomNumberGenerator.Fill(bytes);
        await File.WriteAllBytesAsync(tmp, bytes);
        try
        {
            var version = await _client.PutObjectAsync("big", "large.bin", tmp);
            Assert.EndsWith("-2", version.ETag); // 8 MiB + 1 MiB = 2 parts
            await using var stream = await _client.GetObjectAsync("big", "large.bin");
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(bytes, ms.ToArray());
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task CopyViaHttp()
    {
        await _client.CreateBucketAsync("copy");
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        await File.WriteAllTextAsync(tmp, "copy me");
        try
        {
            await _client.PutObjectAsync("copy", "src.txt", tmp);
            await _client.CopyObjectAsync("copy", "src.txt", "copy", "dst.txt");
            await using var stream = await _client.GetObjectAsync("copy", "dst.txt");
            Assert.Equal("copy me", await new StreamReader(stream).ReadToEndAsync());
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task DeleteNonEmptyBucketFailsViaHttp()
    {
        await _client.CreateBucketAsync("nonempty");
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        await File.WriteAllTextAsync(tmp, "x");
        try
        {
            await _client.PutObjectAsync("nonempty", "k", tmp);
            var ex = await Assert.ThrowsAsync<S3Exception>(() => _client.DeleteBucketAsync("nonempty"));
            Assert.Equal("BucketNotEmpty", ex.Code);
        }
        finally { File.Delete(tmp); }
    }
}
