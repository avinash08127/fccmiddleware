using Amazon.S3;
using Amazon.S3.Model;
using FccMiddleware.Application.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Infrastructure.Storage;

/// <summary>
/// Archives raw FCC payloads to AWS S3 and falls back to the local filesystem when S3 is not configured.
/// Local fallback is only permitted in Development; non-dev environments fail closed.
/// S3 path: s3://{bucket}/raw-payloads/{legalEntityId}/{siteCode}/{year}/{month}/{fccTransactionId}.json
/// </summary>
public sealed class S3RawPayloadArchiver : IRawPayloadArchiver, IDisposable
{
    private readonly string? _bucketName;
    private readonly string? _kmsKeyId;
    private readonly string _localRoot;
    private readonly bool _allowLocalFallback;
    private readonly ILogger<S3RawPayloadArchiver> _logger;
    private readonly AmazonS3Client? _s3Client;

    public S3RawPayloadArchiver(IConfiguration configuration, IHostEnvironment environment, ILogger<S3RawPayloadArchiver> logger)
    {
        _bucketName = configuration["Storage:RawPayloadBucket"];
        _kmsKeyId = configuration["Storage:RawPayloadKmsKeyId"];
        _localRoot = configuration["Storage:RawPayloadLocalRoot"]
            ?? Path.Combine(Path.GetTempPath(), "fccmiddleware-raw-payloads");
        _allowLocalFallback = environment.IsDevelopment();
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_bucketName))
        {
            _s3Client = new AmazonS3Client();
        }
        else if (!_allowLocalFallback)
        {
            throw new InvalidOperationException(
                "Storage:RawPayloadBucket is not configured. " +
                "Local filesystem fallback is only permitted in the Development environment. " +
                "Configure an S3 bucket for raw payload archival in non-development environments.");
        }
    }

    /// <inheritdoc />
    public async Task<string?> ArchiveAsync(
        string legalEntityId,
        string siteCode,
        string fccTransactionId,
        string rawPayload,
        CancellationToken ct = default)
    {
        // S3 path follows the event cold-archive convention from event-schema-design.md
        var now = DateTimeOffset.UtcNow;
        var key = $"raw-payloads/{legalEntityId}/{siteCode}/{now:yyyy}/{now:MM}/{fccTransactionId}.json";

        if (_s3Client is not null && !string.IsNullOrWhiteSpace(_bucketName))
        {
            await using var payloadStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawPayload));
            var request = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = payloadStream,
                ContentType = "application/json",
                AutoCloseStream = false
            };

            if (!string.IsNullOrWhiteSpace(_kmsKeyId))
            {
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
                request.ServerSideEncryptionKeyManagementServiceKeyId = _kmsKeyId;
            }
            else
            {
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
            }

            await _s3Client.PutObjectAsync(request, ct);

            var s3Uri = $"s3://{_bucketName}/{key}";
            _logger.LogDebug("Archived raw payload to {S3Uri}", s3Uri);
            return s3Uri;
        }

        // Local fallback — only reachable in Development (constructor guards non-dev).
        _logger.LogWarning("S3 not configured — archiving raw payload to local filesystem (development only)");

        var fullPath = Path.Combine(_localRoot, key.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, rawPayload, ct);

        var fileUri = new Uri(fullPath).AbsoluteUri;
        _logger.LogDebug("Archived raw payload to local filesystem at {RawPayloadUri}", fileUri);
        return fileUri;
    }

    /// <inheritdoc />
    public async Task<string?> RetrieveAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        try
        {
            if (reference.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
            {
                if (_s3Client is null || string.IsNullOrWhiteSpace(_bucketName))
                    return null;

                var withoutScheme = reference["s3://".Length..];
                var slashIdx = withoutScheme.IndexOf('/');
                if (slashIdx < 0)
                    return null;

                var bucket = withoutScheme[..slashIdx];
                var key = withoutScheme[(slashIdx + 1)..];

                var response = await _s3Client.GetObjectAsync(
                    new Amazon.S3.Model.GetObjectRequest { BucketName = bucket, Key = key }, ct);
                using var reader = new System.IO.StreamReader(response.ResponseStream, System.Text.Encoding.UTF8);
                return await reader.ReadToEndAsync(ct);
            }

            if (reference.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && _allowLocalFallback)
            {
                var localPath = new Uri(reference).LocalPath;
                if (!File.Exists(localPath))
                    return null;

                return await File.ReadAllTextAsync(localPath, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve raw payload from {Reference}", reference);
        }

        return null;
    }

    public void Dispose()
    {
        _s3Client?.Dispose();
    }
}
