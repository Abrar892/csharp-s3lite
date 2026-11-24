namespace S3Lite;

/// <summary>The client-side commands: mb, rb, ls, put, get, rm, cp, presign, versioning, versions, prune.</summary>
public static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var endpoint = Environment.GetEnvironmentVariable("S3LITE_ENDPOINT") ?? "http://127.0.0.1:9000";
        var credentials = KeyStore.LoadClientCredentials();
        if (credentials is null)
        {
            Console.Error.WriteLine("No credentials found. Set S3LITE_ACCESS_KEY/S3LITE_SECRET_KEY,");
            Console.Error.WriteLine("or run `s3lite serve` once so ~/.s3lite/keys.json exists.");
            return 2;
        }

        using var client = new S3Client(endpoint, credentials.Value.AccessKey, credentials.Value.SecretKey);
        try
        {
            return args[0] switch
            {
                "mb" => await MbAsync(client, args),
                "rb" => await RbAsync(client, args),
                "ls" => await LsAsync(client, args),
                "put" => await PutAsync(client, args),
                "get" => await GetAsync(client, args),
                "rm" => await RmAsync(client, args),
                "cp" => await CpAsync(client, args),
                "presign" => Presign(client, args),
                "versioning" => await VersioningAsync(client, args),
                "versions" => await VersionsAsync(client, args),
                "prune" => await PruneAsync(client, args),
                _ => Unknown(args[0]),
            };
        }
        catch (S3Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Code}: {ex.Message}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"error: could not reach {endpoint}: {ex.Message}");
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        return 2;
    }

    private static async Task<int> MbAsync(S3Client client, string[] args)
    {
        if (args.Length < 2) return Usage("mb <bucket>");
        await client.CreateBucketAsync(args[1]);
        Console.WriteLine($"Bucket '{args[1]}' created.");
        return 0;
    }

    private static async Task<int> RbAsync(S3Client client, string[] args)
    {
        if (args.Length < 2) return Usage("rb <bucket>");
        await client.DeleteBucketAsync(args[1]);
        Console.WriteLine($"Bucket '{args[1]}' deleted.");
        return 0;
    }

    private static async Task<int> LsAsync(S3Client client, string[] args)
    {
        string? prefix = null, delimiter = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--prefix" && i + 1 < args.Length) prefix = args[++i];
            else if (args[i] == "--delimiter" && i + 1 < args.Length) delimiter = args[++i];
        }
        var bucket = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--") && a != prefix && a != delimiter);
        if (bucket is null)
        {
            var buckets = await client.ListBucketsAsync();
            foreach (var (name, created) in buckets)
                Console.WriteLine($"{created:yyyy-MM-dd HH:mm:ss}  {name}");
            return 0;
        }

        string? token = null;
        do
        {
            var (objects, prefixes, next) = await client.ListObjectsAsync(bucket, prefix, delimiter, 1000, token);
            foreach (var p in prefixes) Console.WriteLine($"{"",-19}  {p}");
            foreach (var o in objects)
                Console.WriteLine($"{o.LastModified:yyyy-MM-dd HH:mm:ss}  {o.Size,10}  {o.Key}");
            token = next;
        } while (token is not null);
        return 0;
    }

    private static async Task<int> PutAsync(S3Client client, string[] args)
    {
        if (args.Length < 4) return Usage("put <bucket> <key> <file> [--content-type T] [--meta k=v ...]");
        string? contentType = null;
        var metadata = new Dictionary<string, string>();
        for (var i = 4; i < args.Length; i++)
        {
            if (args[i] == "--content-type" && i + 1 < args.Length) contentType = args[++i];
            else if (args[i] == "--meta" && i + 1 < args.Length)
            {
                var kv = args[++i].Split('=', 2);
                if (kv.Length == 2) metadata[kv[0]] = kv[1];
            }
        }
        if (!File.Exists(args[3]))
        {
            Console.Error.WriteLine($"error: file not found: {args[3]}");
            return 1;
        }
        var version = await client.PutObjectAsync(args[1], args[2], args[3], contentType, metadata);
        Console.WriteLine($"Uploaded {args[1]}/{args[2]} ({version.Size} bytes, ETag {version.ETag}).");
        return 0;
    }

    private static async Task<int> GetAsync(S3Client client, string[] args)
    {
        if (args.Length < 3) return Usage("get <bucket> <key> [file] [--version-id V]");
        string? versionId = null;
        for (var i = 3; i < args.Length; i++)
            if (args[i] == "--version-id" && i + 1 < args.Length) versionId = args[++i];
        var output = args.Skip(3).FirstOrDefault(a => !a.StartsWith("--") && a != versionId)
            ?? Path.GetFileName(args[2]);
        await using var stream = await client.GetObjectAsync(args[1], args[2], versionId);
        await using var file = File.Create(output);
        await stream.CopyToAsync(file);
        Console.WriteLine($"Downloaded {args[1]}/{args[2]} to {output}.");
        return 0;
    }

    private static async Task<int> RmAsync(S3Client client, string[] args)
    {
        if (args.Length < 3) return Usage("rm <bucket> <key> [--version-id V]");
        string? versionId = null;
        for (var i = 3; i < args.Length; i++)
            if (args[i] == "--version-id" && i + 1 < args.Length) versionId = args[++i];
        await client.DeleteObjectAsync(args[1], args[2], versionId);
        Console.WriteLine($"Deleted {args[1]}/{args[2]}.");
        return 0;
    }

    private static async Task<int> CpAsync(S3Client client, string[] args)
    {
        if (args.Length < 5) return Usage("cp <src-bucket> <src-key> <dst-bucket> <dst-key>");
        await client.CopyObjectAsync(args[1], args[2], args[3], args[4]);
        Console.WriteLine($"Copied {args[1]}/{args[2]} to {args[3]}/{args[4]}.");
        return 0;
    }

    private static int Presign(S3Client client, string[] args)
    {
        if (args.Length < 3) return Usage("presign <bucket> <key> [--expires 3600]");
        var expires = 3600;
        for (var i = 3; i < args.Length; i++)
            if (args[i] == "--expires" && i + 1 < args.Length) expires = int.Parse(args[++i]);
        Console.WriteLine(client.Presign(args[1], args[2], expires));
        return 0;
    }

    private static async Task<int> VersioningAsync(S3Client client, string[] args)
    {
        if (args.Length < 3) return Usage("versioning <bucket> <Enabled|Suspended>");
        if (args[2] is "Enabled" or "Suspended")
        {
            await client.SetVersioningAsync(args[1], args[2] == "Enabled");
            Console.WriteLine($"Versioning for '{args[1]}' is now {args[2]}.");
            return 0;
        }
        var enabled = await client.GetVersioningAsync(args[1]);
        Console.WriteLine($"Versioning for '{args[1]}': {(enabled ? "Enabled" : "Suspended")}.");
        return 0;
    }

    private static async Task<int> VersionsAsync(S3Client client, string[] args)
    {
        if (args.Length < 2) return Usage("versions <bucket> [prefix]");
        var versions = await client.ListVersionsAsync(args[1], args.Length > 2 ? args[2] : null);
        foreach (var v in versions)
        {
            var marker = v.IsDeleteMarker ? " (delete marker)" : "";
            var latest = v.IsLatest ? " [latest]" : "";
            Console.WriteLine($"{v.LastModified:yyyy-MM-dd HH:mm:ss}  {v.Key}  {v.VersionId}  {v.Size,10}{marker}{latest}");
        }
        return 0;
    }

    private static async Task<int> PruneAsync(S3Client client, string[] args)
    {
        if (args.Length < 2) return Usage("prune <bucket> --keep N");
        var keep = 1;
        for (var i = 2; i < args.Length; i++)
            if (args[i] == "--keep" && i + 1 < args.Length) keep = int.Parse(args[++i]);
        var removed = await client.PruneAsync(args[1], keep);
        Console.WriteLine($"Pruned {removed} old version(s) from '{args[1]}' (kept {keep} noncurrent per key).");
        return 0;
    }

    private static int Usage(string usage)
    {
        Console.Error.WriteLine($"Usage: s3lite {usage}");
        return 2;
    }
}
