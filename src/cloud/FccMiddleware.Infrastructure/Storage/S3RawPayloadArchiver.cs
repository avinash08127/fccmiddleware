using Amazon.S3;
using Amazon.S3.Model;
using FccMiddleware.Application.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Infrastructure.Storage;

/// <summary>
/// Archives raw FCC payloads to AWS S3 and falls back to the local filesystem when S3 is not configured.
/// S3 path: s3://{bucket}/raw-payloads/{legalEntityId}/{siteCode}/{year}/{month}/{fccTransactionId}.json
/// </summary>
public sealed class S3RawPayloadArchiver : IRawPayloadArchiver, IDisposable
{
    private readonly string? _bucketName;
    private readonly string? _kmsKeyId;
    private readonly string _localRoot;
    private readonly ILogger<S3RawPayloadArchiver> _logger;
    private readonly AmazonS3Client? _s3Client;

    public S3RawPayloadArchiver(IConfiguration configuration, ILogger<S3RawPayloadArchiver> logger)
    {
        _bucketName = configuration["Storage:RawPayloadBucket"];
        _kmsKeyId = configuration["Storage:RawPayloadKmsKeyId"];
        _localRoot = configuration["Storage:RawPayloadLocalRoot"]
            ?? Path.Combine(Path.GetTempPath(), "fccmiddleware-raw-payloads");
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_bucketName))
        {
            _s3Client = new AmazonS3Client();
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

    public void Dispose()
    {
        _s3Client?.Dispose();
    }
}
