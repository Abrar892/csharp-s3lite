namespace S3Lite;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("S3Lite 0.1.0 — S3-compatible object storage server (storage engine ready; HTTP server lands in the next patch).");
        Console.WriteLine("Usage: s3lite serve [--port 9000] [--data-dir ~/.s3lite/data]");
        return 0;
    }
}
