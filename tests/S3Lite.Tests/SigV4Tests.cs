using System.Net.Http;
using System.Text;

namespace S3Lite.Tests;

public sealed class SigV4Tests
{
    // AWS's published Signature Version 4 test vectors
    // (docs.aws.amazon.com, "Signature Version 4 Signing Process", IAM example).
    private const string ExampleSecret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private const string ExampleAccessKey = "AKIDEXAMPLE";
    private const string ExampleDate = "20150830T123600Z";
    private const string ExampleDateStamp = "20150830";

    private static List<(string, string)> ExampleHeaders() => new()
    {
        ("content-type", "application/x-www-form-urlencoded; charset=utf-8"),
        ("host", "iam.amazonaws.com"),
        ("x-amz-date", ExampleDate),
    };

    [Fact]
    public void AwsCanonicalRequestVector()
    {
        var canonical = SigV4.BuildCanonicalRequest(
            "GET", "/", "Action=ListUsers&Version=2010-05-08",
            ExampleHeaders(), SigV4.EmptyPayloadHash);
        Assert.Equal("f536975d06c0309214f805bb90ccff089219ecd68b2577efef23edd43b7e1a59",
            SigV4.Sha256Hex(canonical));
    }

    [Fact]
    public void AwsSignatureVector()
    {
        // Same canonical request as AwsCanonicalRequestVector, but with the
        // s3 service scope S3Lite actually uses. The expected signature was
        // computed independently in Python from the AWS-published inputs.
        var canonical = SigV4.BuildCanonicalRequest(
            "GET", "/", "Action=ListUsers&Version=2010-05-08",
            ExampleHeaders(), SigV4.EmptyPayloadHash);
        var stringToSign = SigV4.BuildStringToSign(ExampleDate, ExampleDateStamp, SigV4.Sha256Hex(canonical));
        Assert.Equal("AWS4-HMAC-SHA256\n20150830T123600Z\n20150830/us-east-1/s3/aws4_request\n" +
            "f536975d06c0309214f805bb90ccff089219ecd68b2577efef23edd43b7e1a59", stringToSign);
        Assert.Equal("164b84143bbc7e17d51fb9993534f997b5b5945377be79b60a53a6aa887b3c04",
            SigV4.ComputeSignature(ExampleSecret, ExampleDateStamp, stringToSign));
    }

    [Fact]
    public void SignAndVerifyHeaderAuthRoundtrip()
    {
        var payload = Encoding.UTF8.GetBytes("hello world");
        using var request = new HttpRequestMessage(HttpMethod.Put, "http://127.0.0.1:9000/photos/a.txt");
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        SigV4.SignRequest(request, "AKIDTEST", "test-secret-key", payload,
            now: new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero),
            extraHeaders: new[] { ("content-type", "text/plain") });

        var headers = HeaderMap(request);
        var identity = SigV4.VerifyRequest("PUT", "/photos/a.txt", "", headers, payload,
            accessKey => accessKey == "AKIDTEST" ? "test-secret-key" : null,
            now: new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal("AKIDTEST", identity.AccessKey);
    }

    [Fact]
    public void TamperedSignatureRejected()
    {
        var payload = Encoding.UTF8.GetBytes("hello");
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/photos/a.txt");
        SigV4.SignRequest(request, "AKIDTEST", "test-secret-key", payload,
            now: new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        var headers = HeaderMap(request);
        headers["Authorization"] = headers["Authorization"][..^1] + "0"; // flip last hex digit
        var ex = Assert.Throws<S3Exception>(() =>
            SigV4.VerifyRequest("GET", "/photos/a.txt", "", headers, payload,
                _ => "test-secret-key",
                now: new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("SignatureDoesNotMatch", ex.Code);
    }

    [Fact]
    public void TamperedBodyRejected()
    {
        var payload = Encoding.UTF8.GetBytes("hello");
        using var request = new HttpRequestMessage(HttpMethod.Put, "http://127.0.0.1:9000/photos/a.txt");
        SigV4.SignRequest(request, "AKIDTEST", "test-secret-key", payload,
            now: new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        var headers = HeaderMap(request);
        var ex = Assert.Throws<S3Exception>(() =>
            SigV4.VerifyRequest("PUT", "/photos/a.txt", "", headers, Encoding.UTF8.GetBytes("HELLO"),
                _ => "test-secret-key",
                now: new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("SignatureDoesNotMatch", ex.Code);
    }

    [Fact]
    public void UnknownAccessKeyRejected()
    {
        var payload = Array.Empty<byte>();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/");
        SigV4.SignRequest(request, "AKIDNOPE", "whatever", payload,
            now: new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        var headers = HeaderMap(request);
        var ex = Assert.Throws<S3Exception>(() =>
            SigV4.VerifyRequest("GET", "/", "", headers, payload, _ => null,
                now: new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("InvalidAccessKeyId", ex.Code);
    }

    [Fact]
    public void ExpiredRequestRejected()
    {
        var payload = Array.Empty<byte>();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/");
        SigV4.SignRequest(request, "AKIDTEST", "test-secret-key", payload,
            now: new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        var headers = HeaderMap(request);
        var ex = Assert.Throws<S3Exception>(() =>
            SigV4.VerifyRequest("GET", "/", "", headers, payload, _ => "test-secret-key",
                now: new DateTimeOffset(2026, 11, 24, 12, 30, 0, TimeSpan.Zero)));
        Assert.Equal("AccessDenied", ex.Code);
    }

    [Fact]
    public void PresignAndVerifyRoundtrip()
    {
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var url = SigV4.PresignUrl("http://127.0.0.1:9000", "AKIDTEST", "test-secret-key",
            "GET", "/photos/a.txt", 3600, now);
        var uri = new Uri(url);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = "127.0.0.1:9000",
        };
        var identity = SigV4.VerifyRequest("GET", uri.AbsolutePath, uri.Query, headers, Array.Empty<byte>(),
            accessKey => accessKey == "AKIDTEST" ? "test-secret-key" : null, now);
        Assert.Equal("AKIDTEST", identity.AccessKey);
    }

    [Fact]
    public void ExpiredPresignedRejected()
    {
        var signedAt = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var url = SigV4.PresignUrl("http://127.0.0.1:9000", "AKIDTEST", "test-secret-key",
            "GET", "/photos/a.txt", 60, signedAt);
        var uri = new Uri(url);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = "127.0.0.1:9000",
        };
        var ex = Assert.Throws<S3Exception>(() =>
            SigV4.VerifyRequest("GET", uri.AbsolutePath, uri.Query, headers, Array.Empty<byte>(),
                _ => "test-secret-key", signedAt.AddMinutes(5)));
        Assert.Equal("AccessDenied", ex.Code);
    }

    [Fact]
    public void PresignTamperedQueryRejected()
    {
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var url = SigV4.PresignUrl("http://127.0.0.1:9000", "AKIDTEST", "test-secret-key",
            "GET", "/photos/a.txt", 3600, now);
        var tampered = url.Replace("X-Amz-Expires=3600", "X-Amz-Expires=9999");
        var uri = new Uri(tampered);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = "127.0.0.1:9000",
        };
        var ex = Assert.Throws<S3Exception>(() =>
            SigV4.VerifyRequest("GET", uri.AbsolutePath, uri.Query, headers, Array.Empty<byte>(),
                _ => "test-secret-key", now));
        Assert.Equal("SignatureDoesNotMatch", ex.Code);
    }

    [Fact]
    public void EncodeFollowsRfc3986()
    {
        Assert.Equal("abc-_.~123", SigV4.Encode("abc-_.~123"));
        Assert.Equal("hello%20world", SigV4.Encode("hello world"));
        Assert.Equal("a%2Fb", SigV4.Encode("a/b"));
        Assert.Equal("%C3%A9", SigV4.Encode("é"));
    }

    private static Dictionary<string, string> HeaderMap(HttpRequestMessage request)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
            map[h.Key] = string.Join(",", h.Value);
        if (request.Content is not null)
            foreach (var h in request.Content.Headers)
                map[h.Key] = string.Join(",", h.Value);
        // HttpClient only materialises Host when sending; the signer computed
        // it from the URI, so mirror that here.
        var uri = request.RequestUri!;
        map["host"] = uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
        return map;
    }
}
