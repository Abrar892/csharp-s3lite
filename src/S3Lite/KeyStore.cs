using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace S3Lite;

/// <summary>One API key. Permissions: read, write, admin (admin implies the other two).</summary>
public sealed record ApiKey(string AccessKey, string SecretKey, IReadOnlyList<string> Permissions)
{
    public bool Can(string permission) =>
        Permissions.Contains("admin", StringComparer.OrdinalIgnoreCase)
        || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Server-side key file (~/.s3lite/keys.json) and client credential lookup.</summary>
public static class KeyStore
{
    public static string DefaultConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".s3lite");

    public static string DefaultKeysPath(string? configDir = null) =>
        Path.Combine(configDir ?? DefaultConfigDir, "keys.json");

    /// <summary>
    /// Loads the key file, creating it with a fresh admin keypair on first
    /// run. Returns the keys and whether the file was just created (so the
    /// caller can print the new credentials once).
    /// </summary>
    public static (List<ApiKey> Keys, bool Created) LoadOrCreate(string? configDir = null)
    {
        var dir = configDir ?? DefaultConfigDir;
        var path = Path.Combine(dir, "keys.json");
        Directory.CreateDirectory(dir);
        if (File.Exists(path))
        {
            var keys = JsonSerializer.Deserialize<List<ApiKey>>(File.ReadAllText(path))
                ?? new List<ApiKey>();
            return (keys, false);
        }

        var key = new ApiKey(
            AccessKey: "AKIA" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)),
            SecretKey: GenerateSecret(),
            Permissions: new[] { "admin" });
        var list = new List<ApiKey> { key };
        File.WriteAllText(path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return (list, true);
    }

    private static string GenerateSecret()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = RandomNumberGenerator.GetBytes(40);
        var sb = new StringBuilder(40);
        foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }

    /// <summary>Client credentials, in priority order: env vars, ~/.s3lite/credentials, first key in keys.json.</summary>
    public static (string AccessKey, string SecretKey)? LoadClientCredentials(string? configDir = null)
    {
        var envAccess = Environment.GetEnvironmentVariable("S3LITE_ACCESS_KEY");
        var envSecret = Environment.GetEnvironmentVariable("S3LITE_SECRET_KEY");
        if (!string.IsNullOrEmpty(envAccess) && !string.IsNullOrEmpty(envSecret))
            return (envAccess, envSecret);

        var dir = configDir ?? DefaultConfigDir;
        var credsPath = Path.Combine(dir, "credentials");
        if (File.Exists(credsPath))
        {
            string? access = null, secret = null;
            foreach (var line in File.ReadAllLines(credsPath))
            {
                var kv = line.Split('=', 2);
                if (kv.Length != 2) continue;
                if (kv[0].Trim() == "access_key") access = kv[1].Trim();
                if (kv[0].Trim() == "secret_key") secret = kv[1].Trim();
            }
            if (access is not null && secret is not null) return (access, secret);
        }

        var keysPath = Path.Combine(dir, "keys.json");
        if (File.Exists(keysPath))
        {
            var keys = JsonSerializer.Deserialize<List<ApiKey>>(File.ReadAllText(keysPath));
            if (keys is { Count: > 0 }) return (keys[0].AccessKey, keys[0].SecretKey);
        }
        return null;
    }
}
