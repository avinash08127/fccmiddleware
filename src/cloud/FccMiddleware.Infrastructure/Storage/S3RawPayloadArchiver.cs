using FccMiddleware.Application.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Infrastructure.Storage;

/// <summary>
/// Archives raw FCC payloads to AWS S3.
/// S3 path: s3://{bucket}/raw-payloads/{legalEntityId}/{siteCode}/{year}/{month}/{fccTransactionId}.json
///
/// MVP: when the S3 bucket name is not configured the archiver returns null without error,
/// allowing the ingestion pipeline to continue without a raw payload reference.
/// </summary>
public sealed class S3RawPayloadArchiver : IRawPayloadArchiver
{
    private readonly string? _bucketName;
    private readonly ILogger<S3RawPayloadArchiver> _logger;

    public S3RawPayloadArchiver(IConfiguration configuration, ILogger<S3RawPayloadArchiver> logger)
    {
        _bucketName = configuration["Storage:RawPayloadBucket"];
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<string?> ArchiveAsync(
        string legalEntityId,
        string siteCode,
        string fccTransactionId,
        string rawPayload,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_bucketName))
        {
            _logger.LogDebug("S3 bucket not configured; skipping raw payload archive for {FccTransactionId}", fccTransactionId);
            return Task.FromResult<string?>(null);
        }

        // S3 path follows the event cold-archive convention from event-schema-design.md
        var now = DateTimeOffset.UtcNow;
        var key = $"raw-payloads/{legalEntityId}/{siteCode}/{now:yyyy}/{now:MM}/{fccTransactionId}.json";
        var s3Uri = $"s3://{_bucketName}/{key}";

        // TODO: integrate AWS SDK for S3 — put object with SSE-KMS encryption
        // For now we return the URI that would be used when the SDK is wired in.
        _logger.LogDebug("Would archive payload to {S3Uri}", s3Uri);
        return Task.FromResult<string?>(s3Uri);
    }
}
