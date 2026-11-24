namespace S3Lite;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }

        switch (args[0])
        {
            case "serve":
            {
                var port = Option(args, "--port", "9000");
                var dataDir = Option(args, "--data-dir",
                    Path.Combine(KeyStore.DefaultConfigDir, "data"));
                var configDir = Option(args, "--config-dir", KeyStore.DefaultConfigDir);
                if (!int.TryParse(port, out var portNumber) || portNumber is < 1 or > 65535)
                {
                    Console.Error.WriteLine("Invalid --port (1-65535).");
                    return 2;
                }
                var (keys, created) = KeyStore.LoadOrCreate(configDir);
                if (created)
                {
                    Console.WriteLine("First run: generated an admin keypair and saved it to " +
                        KeyStore.DefaultKeysPath(configDir));
                    Console.WriteLine($"  S3LITE_ACCESS_KEY={keys[0].AccessKey}");
                    Console.WriteLine($"  S3LITE_SECRET_KEY={keys[0].SecretKey}");
                    Console.WriteLine("Keep these somewhere safe — they are the only way in.");
                }
                var store = new ObjectStore(dataDir);
                var server = new HttpServer(store, keys, portNumber);
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                await server.StartAsync(cts.Token);
                return 0;
            }
            default:
                Console.Error.WriteLine($"Unknown command: {args[0]}");
                PrintHelp();
                return 2;
        }
    }

    private static string Option(string[] args, string name, string fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return fallback;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            S3Lite 0.1.0 — S3-compatible object storage server

            Usage:
              s3lite serve [--port 9000] [--data-dir DIR] [--config-dir DIR]
                Start the server. First run prints a fresh admin keypair.

              s3lite mb <bucket>                 Create a bucket
              s3lite rb <bucket>                 Delete an empty bucket
              s3lite ls [bucket] [--prefix P]    List buckets or objects
              s3lite put <bucket> <key> <file>   Upload an object (multipart for large files)
              s3lite get <bucket> <key> [file]   Download an object
              s3lite rm <bucket> <key>           Delete an object
              s3lite cp <src-bucket> <src-key> <dst-bucket> <dst-key>
                                                 Server-side copy
              s3lite presign <bucket> <key> [--expires 3600]
                                                 Print a presigned GET URL
              s3lite versioning <bucket> <Enabled|Suspended>
              s3lite versions <bucket> [prefix]  List all versions of keys
              s3lite prune <bucket> --keep N     Delete old noncurrent versions

            Client credentials come from S3LITE_ACCESS_KEY / S3LITE_SECRET_KEY,
            ~/.s3lite/credentials, or the first key in ~/.s3lite/keys.json.
            Endpoint: S3LITE_ENDPOINT or http://127.0.0.1:9000.
            """);
    }
}
