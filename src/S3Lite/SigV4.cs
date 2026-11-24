using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace S3Lite;

/// <summary>
/// AWS Signature Version 4, implemented by hand against the AWS spec and
/// checked against AWS's own published test vectors (see SigV4Tests).
/// Region is fixed to us-east-1 and the service to s3, as S3Lite is a
/// single-region, single-service server.
/// </summary>
public static class SigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string Region = "us-east-1";
    public const string Service = "s3";
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    // ------------------------------------------------------------ primitives

    public static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    private static byte[] Hmac(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string HmacHex(byte[] key, string data) =>
        Convert.ToHexString(Hmac(key, data)).ToLowerInvariant();

    /// <summary>RFC 3986 percent-encoding: unreserved chars pass through, everything else is %XX uppercase.</summary>
    public static string Encode(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c is '-' or '_' or '.' or '~')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    public static string CanonicalUri(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
            segments[i] = Encode(Uri.UnescapeDataString(segments[i]));
        var joined = string.Join("/", segments);
        return joined.StartsWith('/') ? joined : "/" + joined;
    }

    public static string CanonicalQueryString(IEnumerable<(string Name, string Value)> query) =>
        string.Join("&", query
            .Select(p => (Encode(p.Name), Encode(p.Value)))
            .OrderBy(p => p.Item1, StringComparer.Ordinal)
            .ThenBy(p => p.Item2, StringComparer.Ordinal)
            .Select(p => p.Item1 + "=" + p.Item2));

    /// <summary>Parses a raw query string (with or without leading '?') into decoded pairs.</summary>
    public static List<(string Name, string Value)> ParseQuery(string? query)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrEmpty(query)) return result;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) result.Add((Uri.UnescapeDataString(part), ""));
            else result.Add((Uri.UnescapeDataString(part[..eq]), Uri.UnescapeDataString(part[(eq + 1)..])));
        }
        return result;
    }

    // ------------------------------------------------------------ canonical request

    /// <summary>
    /// Builds the canonical request. Headers must already be lower-cased,
    /// trimmed and sorted by name; SignedHeaders is their ';'-joined names.
    /// </summary>
    public static string BuildCanonicalRequest(string method, string canonicalUri, string canonicalQuery,
        IReadOnlyList<(string Name, string Value)> headers, string payloadHash)
    {
        var sb = new StringBuilder();
        sb.Append(method.ToUpperInvariant()).Append('\n');
        sb.Append(canonicalUri).Append('\n');
        sb.Append(canonicalQuery).Append('\n');
        foreach (var (name, value) in headers)
            sb.Append(name).Append(':').Append(NormalizeHeaderValue(value)).Append('\n');
        sb.Append('\n');
        sb.Append(string.Join(";", headers.Select(h => h.Name))).Append('\n');
        sb.Append(payloadHash);
        return sb.ToString();
    }

    private static string NormalizeHeaderValue(string value)
    {
        // Trim and collapse sequential whitespace to a single space.
        var sb = new StringBuilder();
        var inSpace = false;
        foreach (var c in value.Trim())
        {
            if (c is ' ' or '\t')
            {
                if (!inSpace) sb.Append(' ');
                inSpace = true;
            }
            else
            {
                sb.Append(c);
                inSpace = false;
            }
        }
        return sb.ToString();
    }

    public static string BuildStringToSign(string amzDate, string dateStamp, string hashedCanonicalRequest) =>
        Algorithm + "\n" + amzDate + "\n" + dateStamp + "/" + Region + "/" + Service + "/aws4_request\n" + hashedCanonicalRequest;

    public static byte[] DeriveSigningKey(string secretKey, string dateStamp)
    {
        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        var kRegion = Hmac(kDate, Region);
        var kService = Hmac(kRegion, Service);
        return Hmac(kService, "aws4_request");
    }

    public static string ComputeSignature(string secretKey, string dateStamp, string stringToSign) =>
        HmacHex(DeriveSigningKey(secretKey, dateStamp), stringToSign);

    // ------------------------------------------------------------ client signing

    /// <summary>
    /// Signs an outgoing request in place: sets x-amz-date and
    /// x-amz-content-sha256, then the Authorization header. Returns the
    /// signature. <paramref name="extraHeaders"/> are additional headers to
    /// include in the signature (e.g. content-type, x-amz-meta-*).
    /// </summary>
    public static string SignRequest(HttpRequestMessage request, string accessKey, string secretKey,
        byte[] payload, DateTimeOffset? now = null,
        IEnumerable<(string Name, string Value)>? extraHeaders = null)
    {
        var instant = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var amzDate = instant.ToString("yyyyMMdd'T'HHmmss'Z'");
        var dateStamp = instant.ToString("yyyyMMdd");
        var payloadHash = Sha256Hex(payload);

        request.Headers.Remove("x-amz-date");
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        var host = request.RequestUri!.Host + (request.RequestUri.IsDefaultPort ? "" : ":" + request.RequestUri.Port);
        var headers = new List<(string, string)>
        {
            ("host", host),
            ("x-amz-content-sha256", payloadHash),
            ("x-amz-date", amzDate),
        };
        if (extraHeaders is not null)
            headers.AddRange(extraHeaders);
        headers.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));

        var canonical = BuildCanonicalRequest(
            request.Method.Method,
            CanonicalUri(request.RequestUri.AbsolutePath),
            CanonicalQueryString(ParseQuery(request.RequestUri.Query)),
            headers, payloadHash);
        var stringToSign = BuildStringToSign(amzDate, dateStamp, Sha256Hex(canonical));
        var signature = ComputeSignature(secretKey, dateStamp, stringToSign);

        var signedHeaders = string.Join(";", headers.Select(h => h.Item1));
        var scope = dateStamp + "/" + Region + "/" + Service + "/aws4_request";
        request.Headers.TryAddWithoutValidation("Authorization",
            $"{Algorithm} Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        return signature;
    }

    /// <summary>Builds a presigned URL (query-string auth) for a GET.</summary>
    public static string PresignUrl(string endpoint, string accessKey, string secretKey,
        string method, string path, int expiresSeconds, DateTimeOffset? now = null)
    {
        var instant = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var amzDate = instant.ToString("yyyyMMdd'T'HHmmss'Z'");
        var dateStamp = instant.ToString("yyyyMMdd");
        var scope = dateStamp + "/" + Region + "/" + Service + "/aws4_request";

        var query = new List<(string, string)>
        {
            ("X-Amz-Algorithm", Algorithm),
            ("X-Amz-Credential", accessKey + "/" + scope),
            ("X-Amz-Date", amzDate),
            ("X-Amz-Expires", expiresSeconds.ToString()),
            ("X-Amz-SignedHeaders", "host"),
        };
        var host = new Uri(endpoint).Host;
        var headers = new List<(string, string)> { ("host", host) };
        var canonical = BuildCanonicalRequest(method, CanonicalUri(path),
            CanonicalQueryString(query), headers, UnsignedPayload);
        var stringToSign = BuildStringToSign(amzDate, dateStamp, Sha256Hex(canonical));
        var signature = ComputeSignature(secretKey, dateStamp, stringToSign);

        var signed = CanonicalQueryString(query) + "&X-Amz-Signature=" + signature;
        return endpoint.TrimEnd('/') + CanonicalUri(path) + "?" + signed;
    }

    // ------------------------------------------------------------ server verification

    public sealed record VerifiedIdentity(string AccessKey, IReadOnlyList<string> SignedHeaders);

    /// <summary>
    /// Verifies a request carrying either header auth or presigned-query auth.
    /// <paramref name="headers"/> is the case-insensitive header map as
    /// received; <paramref name="payload"/> is the raw body bytes.
    /// Returns the verified access key, or throws an S3Exception.
    /// </summary>
    public static VerifiedIdentity VerifyRequest(string method, string path, string rawQuery,
        IReadOnlyDictionary<string, string> headers, byte[] payload,
        Func<string, string?> secretLookup, DateTimeOffset? now = null)
    {
        var instant = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var query = ParseQuery(rawQuery);

        var presigned = query.FirstOrDefault(p => p.Name == "X-Amz-Signature");
        if (presigned.Name is not null)
            return VerifyPresigned(method, path, query, headers, instant, secretLookup);

        if (!headers.TryGetValue("Authorization", out var authorization))
            throw S3Exception.AccessDenied("Missing Authorization header.");
        return VerifyHeaderAuth(method, path, query, headers, payload, authorization, instant, secretLookup);
    }

    private static VerifiedIdentity VerifyHeaderAuth(string method, string path,
        List<(string Name, string Value)> query, IReadOnlyDictionary<string, string> headers,
        byte[] payload, string authorization, DateTimeOffset instant,
        Func<string, string?> secretLookup)
    {
        // Authorization: AWS4-HMAC-SHA256 Credential=AKID/yyyymmdd/region/s3/aws4_request, SignedHeaders=a;b, Signature=...
        if (!authorization.StartsWith(Algorithm + " ", StringComparison.Ordinal))
            throw S3Exception.AccessDenied("Unsupported authorization algorithm.");
        var parts = authorization[(Algorithm.Length + 1)..].Split(',', StringSplitOptions.TrimEntries);
        var credential = parts.FirstOrDefault(p => p.StartsWith("Credential=", StringComparison.Ordinal));
        var signedHeadersPart = parts.FirstOrDefault(p => p.StartsWith("SignedHeaders=", StringComparison.Ordinal));
        var signaturePart = parts.FirstOrDefault(p => p.StartsWith("Signature=", StringComparison.Ordinal));
        if (credential is null || signedHeadersPart is null || signaturePart is null)
            throw S3Exception.AccessDenied("Malformed Authorization header.");

        var credentialValue = credential["Credential=".Length..];
        var credentialSegments = credentialValue.Split('/');
        if (credentialSegments.Length != 5 || credentialSegments[3] != Service || credentialSegments[4] != "aws4_request")
            throw S3Exception.AccessDenied("Malformed credential scope.");
        var accessKey = credentialSegments[0];
        var dateStamp = credentialSegments[1];
        var signature = signaturePart["Signature=".Length..];
        var signedHeaderNames = signedHeadersPart["SignedHeaders=".Length..].Split(';');

        if (!headers.TryGetValue("x-amz-date", out var amzDate))
            throw S3Exception.AccessDenied("Missing x-amz-date header.");
        if (!DateTimeOffset.TryParseExact(amzDate, "yyyyMMdd'T'HHmmss'Z'", null,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var requestTime))
            throw S3Exception.AccessDenied("Malformed x-amz-date header.");
        if (Math.Abs((instant - requestTime.ToUniversalTime()).TotalMinutes) > 15)
            throw S3Exception.AccessDenied("Request has expired (x-amz-date is more than 15 minutes old).");

        var secret = secretLookup(accessKey) ?? throw S3Exception.InvalidAccessKeyId();

        var signedHeaders = signedHeaderNames
            .Select(name => (name, headers.TryGetValue(name, out var v) ? v : throw S3Exception.AccessDenied($"Signed header '{name}' is missing.")))
            .OrderBy(h => h.Item1, StringComparer.Ordinal)
            .ToList();

        string payloadHash;
        if (headers.TryGetValue("x-amz-content-sha256", out var claimedHash))
        {
            if (claimedHash == UnsignedPayload)
            {
                payloadHash = UnsignedPayload;
            }
            else
            {
                payloadHash = Sha256Hex(payload);
                if (!string.Equals(payloadHash, claimedHash, StringComparison.OrdinalIgnoreCase))
                    throw S3Exception.SignatureDoesNotMatch("x-amz-content-sha256 does not match the request body.");
            }
        }
        else
        {
            payloadHash = Sha256Hex(payload);
        }

        var canonical = BuildCanonicalRequest(method, CanonicalUri(path),
            CanonicalQueryString(query), signedHeaders, payloadHash);
        var stringToSign = BuildStringToSign(amzDate, dateStamp, Sha256Hex(canonical));
        var expected = ComputeSignature(secret, dateStamp, stringToSign);
        if (!FixedTimeEquals(expected, signature))
            throw S3Exception.SignatureDoesNotMatch();
        return new VerifiedIdentity(accessKey, signedHeaderNames);
    }

    private static VerifiedIdentity VerifyPresigned(string method, string path,
        List<(string Name, string Value)> query, IReadOnlyDictionary<string, string> headers,
        DateTimeOffset instant, Func<string, string?> secretLookup)
    {
        string Get(string name) => query.FirstOrDefault(p => p.Name == name).Value
            ?? throw S3Exception.AccessDenied($"Missing presigned parameter {name}.");

        var algorithm = Get("X-Amz-Algorithm");
        if (algorithm != Algorithm)
            throw S3Exception.AccessDenied("Unsupported presigned algorithm.");
        var credentialValue = Get("X-Amz-Credential");
        var amzDate = Get("X-Amz-Date");
        var expires = int.Parse(Get("X-Amz-Expires"));
        var signedHeadersValue = Get("X-Amz-SignedHeaders");
        var signature = Get("X-Amz-Signature");

        var credentialSegments = credentialValue.Split('/');
        if (credentialSegments.Length != 5)
            throw S3Exception.AccessDenied("Malformed presigned credential scope.");
        var accessKey = credentialSegments[0];
        var dateStamp = credentialSegments[1];

        if (!DateTimeOffset.TryParseExact(amzDate, "yyyyMMdd'T'HHmmss'Z'", null,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var requestTime))
            throw S3Exception.AccessDenied("Malformed X-Amz-Date.");
        if (instant > requestTime.ToUniversalTime().AddSeconds(expires))
            throw S3Exception.AccessDenied("Presigned URL has expired.");

        var secret = secretLookup(accessKey) ?? throw S3Exception.InvalidAccessKeyId();
        var signedQuery = query.Where(p => p.Name != "X-Amz-Signature").ToList();
        var signedHeaderNames = signedHeadersValue.Split(';');
        var signedHeaders = signedHeaderNames
            .Select(name => (name, headers.TryGetValue(name, out var v) ? v : throw S3Exception.AccessDenied($"Signed header '{name}' is missing.")))
            .OrderBy(h => h.Item1, StringComparer.Ordinal)
            .ToList();

        var canonical = BuildCanonicalRequest(method, CanonicalUri(path),
            CanonicalQueryString(signedQuery), signedHeaders, UnsignedPayload);
        var stringToSign = BuildStringToSign(amzDate, dateStamp, Sha256Hex(canonical));
        var expected = ComputeSignature(secret, dateStamp, stringToSign);
        if (!FixedTimeEquals(expected, signature))
            throw S3Exception.SignatureDoesNotMatch();
        return new VerifiedIdentity(accessKey, signedHeaderNames);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
