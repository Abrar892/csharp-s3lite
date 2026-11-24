using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace S3Lite;

/// <summary>
/// The storage engine. Buckets are one JSON manifest each; object bytes live
/// in a content-addressed blob store (SHA-256), so identical content uploaded
/// under different keys is stored once, and deleting a version only removes
/// its blob when nothing else references it.
/// </summary>
public sealed class ObjectStore
{
    private readonly string _dataDir;
    private readonly string _bucketsDir;
    private readonly string _blobsDir;
    private readonly string _uploadsDir;
    private readonly ObjectStoreOptions _options;
    private readonly object _sync = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ObjectStore(string dataDir, ObjectStoreOptions? options = null)
    {
        _dataDir = dataDir;
        _options = options ?? new ObjectStoreOptions();
        _bucketsDir = Path.Combine(dataDir, "buckets");
        _blobsDir = Path.Combine(dataDir, "blobs");
        _uploadsDir = Path.Combine(dataDir, "uploads");
        Directory.CreateDirectory(_bucketsDir);
        Directory.CreateDirectory(_blobsDir);
        Directory.CreateDirectory(_uploadsDir);
    }

    public string DataDir => _dataDir;
    public long MinPartSizeBytes => _options.MinPartSizeBytes;

    // ---------------------------------------------------------------- buckets

    public void CreateBucket(string name)
    {
        ValidateBucketName(name);
        lock (_sync)
        {
            if (BucketManifestExists(name))
                throw S3Exception.BucketAlreadyExists(name);
            var manifest = new BucketManifest
            {
                Name = name,
                Created = DateTimeOffset.UtcNow,
                VersioningEnabled = false,
            };
            SaveManifest(manifest);
        }
    }

    public void DeleteBucket(string name)
    {
        lock (_sync)
        {
            var manifest = LoadManifest(name);
            if (manifest.Versions.Count > 0)
                throw S3Exception.BucketNotEmpty(name);
            File.Delete(ManifestPath(name));
        }
    }

    public IReadOnlyList<BucketManifest> ListBuckets()
    {
        lock (_sync)
        {
            var result = new List<BucketManifest>();
            foreach (var file in Directory.EnumerateFiles(_bucketsDir, "*.json"))
            {
                var manifest = JsonSerializer.Deserialize<BucketManifest>(File.ReadAllText(file), JsonOptions);
                if (manifest is not null) result.Add(manifest);
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return result;
        }
    }

    public BucketManifest GetBucket(string name)
    {
        lock (_sync) { return LoadManifest(name); }
    }

    public void SetVersioning(string bucket, bool enabled)
    {
        lock (_sync)
        {
            var manifest = LoadManifest(bucket);
            manifest.VersioningEnabled = enabled;
            SaveManifest(manifest);
        }
    }

    // ---------------------------------------------------------------- objects

    public ObjectVersion PutObject(string bucket, string key, Stream content, string contentType,
        IReadOnlyDictionary<string, string>? userMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (_sync)
        {
            var manifest = LoadManifest(bucket);
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                content.CopyTo(ms);
                bytes = ms.ToArray();
            }
            if (bytes.LongLength > _options.MaxSinglePutBytes)
                throw S3Exception.InvalidArgument($"Object exceeds the maximum single-PUT size of {_options.MaxSinglePutBytes} bytes; use multipart upload.");

            var blobHash = StoreBlob(bytes);
            var version = NewVersion(bucket, key, blobHash, bytes.LongLength,
                MD5Hex(bytes), contentType, userMetadata);
            UpsertVersion(manifest, version);
            SaveManifest(manifest);
            return version;
        }
    }

    /// <summary>Reads an object's bytes. Returns the stream and the version it came from; the caller owns the stream.</summary>
    public (Stream Content, ObjectVersion Version) GetObject(string bucket, string key, string? versionId = null)
    {
        lock (_sync)
        {
            var version = ResolveVersion(bucket, key, versionId);
            var stream = OpenBlob(version.BlobHash);
            return (stream, version);
        }
    }

    public ObjectVersion HeadObject(string bucket, string key, string? versionId = null)
    {
        lock (_sync) { return ResolveVersion(bucket, key, versionId); }
    }

    public void DeleteObject(string bucket, string key, string? versionId = null)
    {
        lock (_sync)
        {
            var manifest = LoadManifest(bucket);
            if (versionId is not null)
            {
                var target = manifest.Versions.FirstOrDefault(v => v.Key == key && v.VersionId == versionId);
                if (target is null) return; // S3 deletes are idempotent
                manifest.Versions.Remove(target);
                RecomputeLatest(manifest, key);
                SaveManifest(manifest);
                CollectBlobIfOrphaned(manifest, target.BlobHash);
                return;
            }

            if (!manifest.VersioningEnabled)
            {
                var doomed = manifest.Versions.Where(v => v.Key == key).ToList();
                foreach (var v in doomed) manifest.Versions.Remove(v);
                SaveManifest(manifest);
                foreach (var v in doomed) CollectBlobIfOrphaned(manifest, v.BlobHash);
                return;
            }

            // Versioning on: a delete without a version id adds a delete marker.
            var marker = new ObjectVersion
            {
                Key = key,
                VersionId = NewVersionId(),
                IsDeleteMarker = true,
                BlobHash = "",
                Size = 0,
                ETag = "",
                ContentType = "",
                LastModified = DateTimeOffset.UtcNow,
                IsLatest = true,
            };
            UpsertVersion(manifest, marker);
            SaveManifest(manifest);
        }
    }

    public ObjectVersion CopyObject(string srcBucket, string srcKey, string dstBucket, string dstKey,
        string? srcVersionId = null, string? contentType = null,
        IReadOnlyDictionary<string, string>? userMetadata = null)
    {
        lock (_sync)
        {
            var src = ResolveVersion(srcBucket, srcKey, srcVersionId);
            if (src.IsDeleteMarker) throw S3Exception.NoSuchKey(srcKey);
            var dstManifest = LoadManifest(dstBucket);
            var version = NewVersion(dstBucket, dstKey, src.BlobHash, src.Size, src.ETag,
                contentType ?? src.ContentType,
                userMetadata ?? src.UserMetadata);
            UpsertVersion(dstManifest, version);
            SaveManifest(dstManifest);
            return version;
        }
    }

    public (IReadOnlyList<ListedObject> Objects, IReadOnlyList<string> CommonPrefixes, string? NextToken) ListObjects(
        string bucket, string? prefix, string? delimiter, int maxKeys, string? continuationToken)
    {
        if (maxKeys <= 0 || maxKeys > 1000)
            throw S3Exception.InvalidArgument("max-keys must be between 1 and 1000.");
        lock (_sync)
        {
            var manifest = LoadManifest(bucket);
            var latest = manifest.Versions
                .Where(v => v.IsLatest && !v.IsDeleteMarker)
                .Where(v => prefix is null || v.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(v => v.Key, StringComparer.Ordinal)
                .ToList();

            // continuationToken is the last key returned on the previous page.
            var start = 0;
            if (continuationToken is not null)
            {
                start = latest.FindIndex(v => string.CompareOrdinal(v.Key, continuationToken) > 0);
                if (start < 0) start = latest.Count;
            }

            var objects = new List<ListedObject>();
            var prefixes = new SortedSet<string>(StringComparer.Ordinal);
            string? next = null;
            var emitted = 0;
            for (var i = start; i < latest.Count; i++)
            {
                var v = latest[i];
                if (delimiter is not null)
                {
                    var rest = prefix is null ? v.Key : v.Key[prefix.Length..];
                    var at = rest.IndexOf(delimiter, StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        prefixes.Add(v.Key[..((prefix?.Length ?? 0) + at + delimiter.Length)]);
                        continue;
                    }
                }
                if (emitted == maxKeys)
                {
                    next = latest[i - 1].Key;
                    break;
                }
                objects.Add(new ListedObject
                {
                    Key = v.Key,
                    ETag = v.ETag,
                    Size = v.Size,
                    LastModified = v.LastModified,
                });
                emitted++;
            }
            // If we stopped exactly at the end, there is no next page.
            if (next is not null && start + emitted >= latest.Count) next = null;
            return (objects, prefixes.ToList(), next);
        }
    }

    public IReadOnlyList<ObjectVersion> ListVersions(string bucket, string? prefix, int maxKeys = 1000)
    {
        lock (_sync)
        {
            var manifest = LoadManifest(bucket);
            return manifest.Versions
                .Where(v => prefix is null || v.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(v => v.Key, StringComparer.Ordinal)
                .ThenByDescending(v => v.LastModified)
                .Take(maxKeys)
                .Select(v => v.Clone())
                .ToList();
        }
    }

    /// <summary>Deletes noncurrent versions beyond <paramref name="keep"/> per key. Returns the number removed.</summary>
    public int PruneVersions(string bucket, int keep)
    {
        if (keep < 0) throw S3Exception.InvalidArgument("keep must be >= 0.");
        lock (_sync)
        {
            var manifest = LoadManifest(bucket);
            var removed = 0;
            foreach (var group in manifest.Versions.GroupBy(v => v.Key).ToList())
            {
                var noncurrent = group.Where(v => !v.IsLatest)
                    .OrderByDescending(v => v.LastModified)
                    .ToList();
                foreach (var stale in noncurrent.Skip(keep))
                {
                    manifest.Versions.Remove(stale);
                    removed++;
                    CollectBlobIfOrphaned(manifest, stale.BlobHash);
                }
            }
            if (removed > 0) SaveManifest(manifest);
            return removed;
        }
    }

    // ---------------------------------------------------------------- multipart

    public MultipartUpload InitiateMultipart(string bucket, string key, string contentType,
        IReadOnlyDictionary<string, string>? userMetadata = null)
    {
        lock (_sync)
        {
            _ = LoadManifest(bucket); // throws NoSuchBucket if missing
            var upload = new MultipartUpload
            {
                UploadId = Guid.NewGuid().ToString("N"),
                Bucket = bucket,
                Key = key,
                ContentType = contentType,
                UserMetadata = userMetadata is null ? new() : new Dictionary<string, string>(userMetadata),
                Initiated = DateTimeOffset.UtcNow,
            };
            SaveUpload(upload);
            return upload;
        }
    }

    public MultipartPart UploadPart(string uploadId, int partNumber, Stream content)
    {
        if (partNumber < 1 || partNumber > 10000)
            throw S3Exception.InvalidArgument("partNumber must be between 1 and 10000.");
        lock (_sync)
        {
            var upload = LoadUpload(uploadId);
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                content.CopyTo(ms);
                bytes = ms.ToArray();
            }
            var blobHash = StoreBlob(bytes);
            var part = new MultipartPart
            {
                PartNumber = partNumber,
                ETag = MD5Hex(bytes),
                BlobHash = blobHash,
                Size = bytes.LongLength,
            };
            upload.Parts[partNumber] = part;
            SaveUpload(upload);
            return part;
        }
    }

    public IReadOnlyList<MultipartPart> ListParts(string uploadId)
    {
        lock (_sync)
        {
            var upload = LoadUpload(uploadId);
            return upload.Parts.Values.OrderBy(p => p.PartNumber).ToList();
        }
    }

    public ObjectVersion CompleteMultipart(string uploadId, IReadOnlyList<(int PartNumber, string ETag)> parts)
    {
        lock (_sync)
        {
            var upload = LoadUpload(uploadId);
            if (parts.Count == 0)
                throw S3Exception.InvalidPart("CompleteMultipartUpload requires at least one part.");

            var ordered = parts.OrderBy(p => p.PartNumber).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].PartNumber != i + 1)
                    throw S3Exception.InvalidPart($"Parts must be numbered consecutively from 1; got {ordered[i].PartNumber} at position {i + 1}.");
                if (!upload.Parts.TryGetValue(ordered[i].PartNumber, out var stored))
                    throw S3Exception.InvalidPart($"Part {ordered[i].PartNumber} was never uploaded.");
                if (!string.Equals(stored.ETag, ordered[i].ETag.Trim('"'), StringComparison.OrdinalIgnoreCase))
                    throw S3Exception.InvalidPart($"ETag mismatch for part {ordered[i].PartNumber}.");
                if (i < ordered.Count - 1 && stored.Size < _options.MinPartSizeBytes)
                    throw S3Exception.InvalidPart($"Part {ordered[i].PartNumber} is {stored.Size} bytes; all but the last part must be at least {_options.MinPartSizeBytes} bytes.");
            }

            // Assemble: concatenate part bytes into one blob.
            byte[] assembled;
            using (var ms = new MemoryStream())
            {
                foreach (var part in ordered.Select(p => upload.Parts[p.PartNumber]))
                {
                    using var partStream = OpenBlob(part.BlobHash);
                    partStream.CopyTo(ms);
                }
                assembled = ms.ToArray();
            }
            var blobHash = StoreBlob(assembled);

            // S3 multipart ETag: MD5 of the concatenated raw part MD5s, suffixed with "-N".
            using var md5 = MD5.Create();
            foreach (var p in ordered)
            {
                var raw = Convert.FromHexString(upload.Parts[p.PartNumber].ETag);
                md5.TransformBlock(raw, 0, raw.Length, null, 0);
            }
            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var eTag = Convert.ToHexString(md5.Hash!).ToLowerInvariant() + "-" + ordered.Count;

            var manifest = LoadManifest(upload.Bucket);
            var version = NewVersion(upload.Bucket, upload.Key, blobHash, assembled.LongLength,
                eTag, upload.ContentType, upload.UserMetadata);
            UpsertVersion(manifest, version);
            SaveManifest(manifest);

            // Clean up: the upload state goes, and part blobs that nothing references go too.
            File.Delete(UploadPath(uploadId));
            foreach (var p in upload.Parts.Values)
                CollectBlobIfOrphaned(manifest, p.BlobHash);
            return version;
        }
    }

    public void AbortMultipart(string uploadId)
    {
        lock (_sync)
        {
            var upload = LoadUpload(uploadId);
            File.Delete(UploadPath(uploadId));
            // Part blobs are orphaned unless referenced by a stored version; sweep them.
            var manifests = ListBuckets().Select(m => m.Name).ToList();
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in manifests)
            {
                foreach (var v in LoadManifest(name).Versions)
                    if (v.BlobHash.Length > 0) referenced.Add(v.BlobHash);
            }
            foreach (var part in upload.Parts.Values)
            {
                if (!referenced.Contains(part.BlobHash))
                    DeleteBlob(part.BlobHash);
            }
        }
    }

    // ---------------------------------------------------------------- internals

    private ObjectVersion NewVersion(string bucket, string key, string blobHash, long size,
        string eTag, string contentType, IReadOnlyDictionary<string, string>? userMetadata) => new()
    {
        Key = key,
        VersionId = NewVersionId(),
        IsDeleteMarker = false,
        BlobHash = blobHash,
        Size = size,
        ETag = eTag,
        ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
        LastModified = DateTimeOffset.UtcNow,
        UserMetadata = userMetadata is null ? new() : new Dictionary<string, string>(userMetadata),
        IsLatest = true,
    };

    private static string NewVersionId() => Guid.NewGuid().ToString("N");

    private void UpsertVersion(BucketManifest manifest, ObjectVersion version)
    {
        foreach (var existing in manifest.Versions.Where(v => v.Key == version.Key))
            existing.IsLatest = false;
        version.IsLatest = true;
        manifest.Versions.Add(version);
        if (!manifest.VersioningEnabled)
        {
            // Without versioning, only the newest version of a key survives.
            var stale = manifest.Versions.Where(v => v.Key == version.Key && v.VersionId != version.VersionId).ToList();
            foreach (var s in stale) manifest.Versions.Remove(s);
            foreach (var s in stale) CollectBlobIfOrphaned(manifest, s.BlobHash);
        }
    }

    private void RecomputeLatest(BucketManifest manifest, string key)
    {
        var remaining = manifest.Versions.Where(v => v.Key == key)
            .OrderByDescending(v => v.LastModified).ToList();
        foreach (var v in remaining) v.IsLatest = false;
        if (remaining.Count > 0) remaining[0].IsLatest = true;
    }

    private ObjectVersion ResolveVersion(string bucket, string key, string? versionId)
    {
        var manifest = LoadManifest(bucket);
        ObjectVersion? found;
        if (versionId is not null)
        {
            found = manifest.Versions.FirstOrDefault(v => v.Key == key && v.VersionId == versionId);
            if (found is null) throw S3Exception.NoSuchKey(key);
            if (found.IsDeleteMarker) throw S3Exception.NoSuchKey(key);
            return found;
        }
        found = manifest.Versions.FirstOrDefault(v => v.Key == key && v.IsLatest);
        if (found is null || found.IsDeleteMarker) throw S3Exception.NoSuchKey(key);
        return found;
    }

    private string StoreBlob(byte[] bytes)
    {
        var hash = SHA256Hex(bytes);
        var path = BlobPath(hash);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path);
        }
        return hash;
    }

    private FileStream OpenBlob(string hash) =>
        new(BlobPath(hash), FileMode.Open, FileAccess.Read, FileShare.Read);

    private void DeleteBlob(string hash)
    {
        var path = BlobPath(hash);
        if (File.Exists(path)) File.Delete(path);
    }

    private void CollectBlobIfOrphaned(BucketManifest excludingManifest, string blobHash)
    {
        if (string.IsNullOrEmpty(blobHash)) return;
        // The caller already removed the version from excludingManifest; check every manifest.
        foreach (var file in Directory.EnumerateFiles(_bucketsDir, "*.json"))
        {
            var manifest = JsonSerializer.Deserialize<BucketManifest>(File.ReadAllText(file), JsonOptions);
            if (manifest is null) continue;
            if (manifest.Versions.Any(v => v.BlobHash == blobHash)) return;
        }
        // Also check in-flight multipart parts.
        foreach (var file in Directory.EnumerateFiles(_uploadsDir, "*.json"))
        {
            var upload = JsonSerializer.Deserialize<MultipartUpload>(File.ReadAllText(file), JsonOptions);
            if (upload is null) continue;
            if (upload.Parts.Values.Any(p => p.BlobHash == blobHash)) return;
        }
        DeleteBlob(blobHash);
    }

    private string BlobPath(string hash) =>
        Path.Combine(_blobsDir, hash[..2], hash[2..4], hash);

    private static string SHA256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string MD5Hex(byte[] bytes) =>
        Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();

    private string ManifestPath(string bucket) => Path.Combine(_bucketsDir, bucket + ".json");
    private string UploadPath(string uploadId) => Path.Combine(_uploadsDir, uploadId + ".json");

    private bool BucketManifestExists(string bucket) => File.Exists(ManifestPath(bucket));

    private BucketManifest LoadManifest(string bucket)
    {
        var path = ManifestPath(bucket);
        if (!File.Exists(path)) throw S3Exception.NoSuchBucket(bucket);
        var manifest = JsonSerializer.Deserialize<BucketManifest>(File.ReadAllText(path), JsonOptions);
        return manifest ?? throw new InvalidOperationException($"Corrupt manifest for bucket {bucket}.");
    }

    private void SaveManifest(BucketManifest manifest)
    {
        var path = ManifestPath(manifest.Name);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private MultipartUpload LoadUpload(string uploadId)
    {
        var path = UploadPath(uploadId);
        if (!File.Exists(path)) throw S3Exception.NoSuchUpload(uploadId);
        var upload = JsonSerializer.Deserialize<MultipartUpload>(File.ReadAllText(path), JsonOptions);
        return upload ?? throw new InvalidOperationException($"Corrupt upload state {uploadId}.");
    }

    private void SaveUpload(MultipartUpload upload)
    {
        var path = UploadPath(upload.UploadId);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, JsonSerializer.Serialize(upload, JsonOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static void ValidateBucketName(string name)
    {
        // S3 bucket naming rules (the subset that matters for a path-style server).
        var ok = name.Length is >= 3 and <= 63
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
            && char.IsAsciiLetterOrDigit(name[0])
            && char.IsAsciiLetterOrDigit(name[^1])
            && !name.Contains("..")
            && !name.StartsWith("xn--", StringComparison.Ordinal);
        if (!ok) throw S3Exception.InvalidBucketName(name);
    }
}
