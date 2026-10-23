using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Api.Services.Storage;

public class StorageOptions
{
    public const string Section = "Storage";
    /// <summary>S3 endpoint as the API reaches it (compose: http://storage:8333).</summary>
    public string Endpoint { get; set; } = "http://localhost:8333";
    /// <summary>
    /// Base URL the browser uses for signed URLs (the site itself, e.g. https://twin.example.hu). The reverse proxy
    /// forwards /&lt;bucket&gt;/* to the object store with the Host header unchanged, so the signature stays valid and
    /// no CORS is needed. Signed URLs are path-style: {PublicUrl}/{Bucket}/{key}.
    /// </summary>
    public string PublicUrl { get; set; } = "http://localhost:8333";
    /// <summary>Also the URL path the reverse proxy reserves for the store (/objects/*): must not clash with UI paths (the Angular build uses /media/ for fonts).</summary>
    public string Bucket { get; set; } = "objects";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public int UploadUrlMinutes { get; set; } = 60;
    public int DownloadUrlMinutes { get; set; } = 15;
    public int MaxPanoramaMb { get; set; } = 300;
    /// <summary>Total size of one 3D model upload (a GLB or all files of a 3D Tiles package).</summary>
    public int MaxModelMb { get; set; } = 4096;
    /// <summary>Lifetime of the signed tile URLs inside a served tileset.json (tiles load lazily while the model is open).</summary>
    public int TileUrlMinutes { get; set; } = 240;
}

/// <summary>
/// S3-compatible object store (SeaweedFS by default, spec 6.2). Binary content never goes into the database and never
/// passes the API in a request (NFR-3): the browser uploads and downloads with short-lived signed URLs; only the
/// background processing (privacy blurring, display version) reads and writes objects server-side.
/// </summary>
public interface IObjectStorage
{
    string PresignPut(string key, string contentType);
    string PresignGet(string key);
    /// <summary>A signed GET valid for <paramref name="lifetime"/> (3D tiles are loaded lazily while the model is viewed).</summary>
    string PresignGet(string key, TimeSpan lifetime);
    Task<long?> SizeAsync(string key, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    Task PutAsync(string key, Stream content, string contentType, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task<List<string>> ListAsync(string prefix, CancellationToken ct);
    Task DeletePrefixAsync(string prefix, CancellationToken ct);
    Task EnsureBucketAsync(CancellationToken ct);
}

public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly StorageOptions _options;
    /// <summary>Server-side operations on the internal endpoint.</summary>
    private readonly AmazonS3Client _internal;
    /// <summary>Only signs URLs (offline) for the public base URL; never sends requests.</summary>
    private readonly AmazonS3Client _public;

    public S3ObjectStorage(IOptions<StorageOptions> options)
    {
        _options = options.Value;
        var credentials = new BasicAWSCredentials(_options.AccessKey, _options.SecretKey);
        _internal = new AmazonS3Client(credentials, Config(_options.Endpoint));
        _public = new AmazonS3Client(credentials, Config(_options.PublicUrl));
    }

    private AmazonS3Config Config(string url) => new()
    {
        ServiceURL = url,
        ForcePathStyle = true,
        AuthenticationRegion = _options.Region,
        // Flexible checksums (CRC32 trailers) of the v4 SDK are not needed and not supported by every S3 server.
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    };

    public string PresignPut(string key, string contentType) => _public.GetPreSignedURL(new GetPreSignedUrlRequest
    {
        BucketName = _options.Bucket,
        Key = key,
        Verb = HttpVerb.PUT,
        ContentType = contentType,
        Expires = DateTime.UtcNow.AddMinutes(_options.UploadUrlMinutes),
        Protocol = Protocol(_options.PublicUrl),
    });

    public string PresignGet(string key) => PresignGet(key, TimeSpan.FromMinutes(_options.DownloadUrlMinutes));

    public string PresignGet(string key, TimeSpan lifetime) => _public.GetPreSignedURL(new GetPreSignedUrlRequest
    {
        BucketName = _options.Bucket,
        Key = key,
        Verb = HttpVerb.GET,
        Expires = DateTime.UtcNow.Add(lifetime),
        Protocol = Protocol(_options.PublicUrl),
    });

    public async Task<List<string>> ListAsync(string prefix, CancellationToken ct)
    {
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = _options.Bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await _internal.ListObjectsV2Async(request, ct);
            keys.AddRange((response.S3Objects ?? []).Select(o => o.Key));
            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);
        return keys;
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        foreach (var key in await ListAsync(prefix, ct)) await DeleteAsync(key, ct);
    }

    public async Task<long?> SizeAsync(string key, CancellationToken ct)
    {
        try
        {
            var meta = await _internal.GetObjectMetadataAsync(_options.Bucket, key, ct);
            return meta.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var response = await _internal.GetObjectAsync(_options.Bucket, key, ct);
        return response.ResponseStream;
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct) =>
        await _internal.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            UseChunkEncoding = false,
        }, ct);

    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await _internal.DeleteObjectAsync(_options.Bucket, key, ct);

    public async Task EnsureBucketAsync(CancellationToken ct)
    {
        try
        {
            await _internal.PutBucketAsync(_options.Bucket, ct);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
    }

    private static Amazon.S3.Protocol Protocol(string url) =>
        url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? Amazon.S3.Protocol.HTTPS : Amazon.S3.Protocol.HTTP;

    public void Dispose()
    {
        _internal.Dispose();
        _public.Dispose();
    }
}
