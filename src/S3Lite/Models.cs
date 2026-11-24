namespace S3Lite;

/// <summary>An error that maps directly onto an S3 error response.</summary>
public sealed class S3Exception : Exception
{
    public S3Exception(string code, int httpStatus, string message) : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
    }

    public string Code { get; }
    public int HttpStatus { get; }

    public static S3Exception NoSuchBucket(string bucket) =>
        new("NoSuchBucket", 404, $"The specified bucket does not exist: {bucket}");

    public static S3Exception NoSuchKey(string key) =>
        new("NoSuchKey", 404, $"The specified key does not exist: {key}");

    public static S3Exception BucketAlreadyExists(string bucket) =>
        new("BucketAlreadyExists", 409, $"The requested bucket name is not available: {bucket}");

    public static S3Exception BucketNotEmpty(string bucket) =>
        new("BucketNotEmpty", 409, $"The bucket you tried to delete is not empty: {bucket}");

    public static S3Exception InvalidBucketName(string bucket) =>
        new("InvalidBucketName", 400, $"The specified bucket is not valid: {bucket}");

    public static S3Exception InvalidPart(string message) =>
        new("InvalidPart", 400, message);

    public static S3Exception NoSuchUpload(string uploadId) =>
        new("NoSuchUpload", 404, $"The specified multipart upload does not exist: {uploadId}");

    public static S3Exception InvalidArgument(string message) =>
        new("InvalidArgument", 400, message);

    public static S3Exception AccessDenied(string message = "Access Denied") =>
        new("AccessDenied", 403, message);

    public static S3Exception SignatureDoesNotMatch(string message = "The request signature we calculated does not match the signature you provided.") =>
        new("SignatureDoesNotMatch", 403, message);

    public static S3Exception InvalidAccessKeyId(string message = "The AWS Access Key Id you provided does not exist in our records.") =>
        new("InvalidAccessKeyId", 403, message);
}

/// <summary>One version of one object key inside a bucket manifest.</summary>
public sealed class ObjectVersion
{
    public required string Key { get; set; }
    public required string VersionId { get; set; }
    public required bool IsDeleteMarker { get; set; }
    public required string BlobHash { get; set; } // SHA-256 hex of the bytes; "" for delete markers
    public required long Size { get; set; }
    public required string ETag { get; set; } // S3-style ETag, quoted when rendered
    public required string ContentType { get; set; }
    public required DateTimeOffset LastModified { get; set; }
    public Dictionary<string, string> UserMetadata { get; set; } = new();
    public required bool IsLatest { get; set; }

    public ObjectVersion Clone() => new()
    {
        Key = Key,
        VersionId = VersionId,
        IsDeleteMarker = IsDeleteMarker,
        BlobHash = BlobHash,
        Size = Size,
        ETag = ETag,
        ContentType = ContentType,
        LastModified = LastModified,
        UserMetadata = new Dictionary<string, string>(UserMetadata),
        IsLatest = IsLatest,
    };
}

/// <summary>A bucket's durable manifest: one JSON document per bucket.</summary>
public sealed class BucketManifest
{
    public required string Name { get; set; }
    public required DateTimeOffset Created { get; set; }
    public required bool VersioningEnabled { get; set; }
    public List<ObjectVersion> Versions { get; set; } = new();
}

/// <summary>One uploaded part of a multipart upload.</summary>
public sealed class MultipartPart
{
    public required int PartNumber { get; set; }
    public required string ETag { get; set; } // unquoted MD5 hex of the part bytes
    public required string BlobHash { get; set; }
    public required long Size { get; set; }
}

/// <summary>Durable state for an in-progress multipart upload.</summary>
public sealed class MultipartUpload
{
    public required string UploadId { get; set; }
    public required string Bucket { get; set; }
    public required string Key { get; set; }
    public required string ContentType { get; set; }
    public Dictionary<string, string> UserMetadata { get; set; } = new();
    public required DateTimeOffset Initiated { get; set; }
    public Dictionary<int, MultipartPart> Parts { get; set; } = new();
}

/// <summary>A listed object (latest version only) for ListObjectsV2.</summary>
public sealed class ListedObject
{
    public required string Key { get; set; }
    public required string ETag { get; set; }
    public required long Size { get; set; }
    public required DateTimeOffset LastModified { get; set; }
}

/// <summary>Options that shape the store's behaviour. Defaults follow S3.</summary>
public sealed class ObjectStoreOptions
{
    /// <summary>Minimum size of a non-final multipart part. S3 requires 5 MiB; tests use smaller values.</summary>
    public long MinPartSizeBytes { get; set; } = 5L * 1024 * 1024;

    /// <summary>Maximum object size accepted in a single PUT (S3: 5 GiB). Kept lower here to stay honest about a demo server.</summary>
    public long MaxSinglePutBytes { get; set; } = 512L * 1024 * 1024;
}
