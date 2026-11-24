# S3Lite — S3-compatible object storage server

S3Lite is a small, self-contained object storage server that speaks enough of
the Amazon S3 API to be useful: buckets, objects, versioning, multipart
uploads, presigned URLs, and AWS Signature Version 4 authentication. It is a
single .NET 8 console app with **zero runtime dependencies** — no SDK, no
database, no packages at runtime. Point any S3-compatible client at it (the
bundled `s3lite` CLI, the AWS CLI with a custom endpoint, or your own code)
and it behaves like a local S3.

## Why this exists

Object storage looks simple from the outside — `PUT` some bytes, `GET` them
back — but the interesting parts are the ones you never see: how versions are
kept, how a 5 GB upload survives a dropped connection, how a request proves
it came from someone holding the secret key. S3Lite implements those parts
by hand, against the real S3 wire format, so the whole thing fits in your
head and in one repository.

## Features

- **Buckets and objects** — create/delete/list buckets; put/get/head/delete
  objects with content types and `x-amz-meta-*` user metadata.
- **Versioning** — enable it per bucket and every overwrite becomes a new
  version; deletes leave a delete marker instead of destroying history.
  Fetch any version by id, list all versions, prune old ones.
- **Multipart uploads** — initiate, upload parts in any order, complete or
  abort. Parts are stored content-addressed; completion assembles them and
  produces the standard S3 multipart ETag (`md5-of-md5s-N`).
- **AWS Signature Version 4** — header auth and presigned query URLs,
  verified against the official AWS test vectors. Keys live in
  `~/.s3lite/keys.json`; the server prints a fresh keypair on first run.
- **Content-addressed blob store** — object bytes are stored once, by
  SHA-256. Uploading the same content under ten keys costs one copy on
  disk, and deleting a version only frees its bytes when nothing else
  references them.
- **Range reads** — `Range: bytes=a-b` on GET, for resumable downloads.
- **CLI client** — `mb`, `ls`, `put`, `get`, `rm`, `cp`, `presign`,
  `versioning`, `versions`, `prune`; large files are uploaded via
  multipart automatically.

## Design decisions

1. **One JSON manifest per bucket, written atomically.** A bucket's whole
   state (versions, markers, metadata) is a single JSON document, saved
   via write-to-temp-then-move. A crash mid-save can never leave a
   half-written manifest, and the file stays human-readable for debugging.
2. **Blobs are immutable and content-addressed.** Object bytes are files
   named by their SHA-256, sharded two levels deep. Versions reference
   blobs by hash, never by path, which makes copy a metadata-only
   operation and deduplication automatic.
3. **A single lock guards the store.** Correctness first: every public
   store operation takes one lock, so manifests, blobs and upload state
   can never disagree. A single-node demo server does not need finer
   locking, and one lock is one lock to reason about.
4. **SigV4 is implemented, not assumed.** Signing and verification are
   hand-written against the AWS spec and checked against AWS's own test
   vectors (canonical request hash
   `f536975d…e1a59`, signature `5d672d79…2b5d7`). Any client that signs
   correctly is accepted; any tampered request is rejected.
5. **Zero runtime dependencies.** The HTTP layer is `HttpListener`, the
   XML is `XDocument`, the crypto is `System.Security.Cryptography`.
   Nothing to install, nothing to keep up to date.

## Quick start

```bash
# Start the server (first run prints a fresh access keypair — keep it)
dotnet run --project src/S3Lite -- serve

# In another terminal, export the printed keys
export S3LITE_ACCESS_KEY=AKIAXXXXXXXXXXXXXXXX
export S3LITE_SECRET_KEY=xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx

# Buckets and objects
dotnet run --project src/S3Lite -- mb photos
dotnet run --project src/S3Lite -- put photos cat.jpg ./cat.jpg
dotnet run --project src/S3Lite -- ls photos
dotnet run --project src/S3Lite -- get photos cat.jpg ./cat-back.jpg

# A shareable link that expires in an hour
dotnet run --project src/S3Lite -- presign photos cat.jpg --expires 3600
```

Or build once and use the binary (the .NET runtime must be on your PATH,
e.g. `export PATH="$HOME/workspace/.dotnet:$PATH"` if you installed it there):

```bash
dotnet build -c Release
./src/S3Lite/bin/Release/net8.0/s3lite serve --port 9000 --data-dir ~/.s3lite/data
```

## Server endpoints

Path-style S3 API on `http://127.0.0.1:9000` by default:

| Operation | Request |
| --- | --- |
| List buckets | `GET /` |
| Create / delete bucket | `PUT /{bucket}`, `DELETE /{bucket}` |
| List objects (V2) | `GET /{bucket}?list-type=2&prefix=&delimiter=&max-keys=&continuation-token=` |
| List versions | `GET /{bucket}?versions` |
| Get / set versioning | `GET /{bucket}?versioning`, `PUT /{bucket}?versioning` |
| Put / get / head / delete object | `PUT`, `GET`, `HEAD`, `DELETE /{bucket}/{key}` |
| Copy object | `PUT /{bucket}/{key}` with `x-amz-copy-source: /src-bucket/src-key` |
| Multipart | `POST /{bucket}/{key}?uploads`, `PUT ...?partNumber=N&uploadId=U`, `POST ...?uploadId=U`, `DELETE ...?uploadId=U`, `GET ...?uploads` (list parts) |
| Prune old versions *(S3Lite extension)* | `POST /{bucket}?prune&keep=N` (admin key required) |

## Configuration

- **Data directory** — `--data-dir` (default `~/.s3lite/data`): buckets,
  blobs and multipart state live here. It is safe to back up by copying
  the directory while the server is stopped.
- **Keys** — `~/.s3lite/keys.json`. Each key has `read`, `write` and/or
  `admin` permissions. The server generates one admin keypair on first
  run and prints it; add more keys by editing the file and restarting.
- **Port** — `--port` (default `9000`).

## Tests

```bash
dotnet test
```

53 xUnit tests cover the store (versioning, dedup, delete markers,
pruning), SigV4 (official AWS vectors, tamper and expiry cases),
multipart (out-of-order parts, ETag, abort, minimum part size) and the
HTTP layer end to end against a live server on localhost.

## What it is not

S3Lite is a learning project and a local dev stand-in, not a production
store. It keeps all manifests in memory-mapped JSON, holds one global
lock, has no replication, no TLS, and no virtual-host-style addressing.
If you need durability guarantees or multi-node operation, use real S3
or MinIO.
