using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Infrastructure.Storage;

public interface IArchiveObjectStore
{
    Task<string> PutObjectAsync(string key, string contentType, Stream content, CancellationToken ct);
    Task<string> PutTextAsync(string key, string contentType, string content, CancellationToken ct);
}

/// <summary>
/// Stores archive artifacts in S3 when configured and falls back to the local filesystem for dev/test.
/// </summary>
public sealed class ArchiveObjectStore : IArchiveObjectStore, IDisposable
{
    private readonly string? _bucketName;
    private readonly string? _kmsKeyId;
    private readonly string _localRoot;
    private readonly ILogger<ArchiveObjectStore> _logger;
    private readonly AmazonS3Client? _s3Client;

    public ArchiveObjectStore(IConfiguration configuration, ILogger<ArchiveObjectStore> logger)
    {
        _bucketName = configuration["Storage:ArchiveBucket"];
        _kmsKeyId = configuration["Storage:ArchiveKmsKeyId"];
        _localRoot = configuration["Storage:ArchiveLocalRoot"]
            ?? Path.Combine(Path.GetTempPath(), "fccmiddleware-archive");
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_bucketName))
        {
            _s3Client = new AmazonS3Client();
        }
    }

    public async Task<string> PutObjectAsync(string key, string contentType, Stream content, CancellationToken ct)
    {
        if (_s3Client is not null && !string.IsNullOrWhiteSpace(_bucketName))
        {
            content.Position = 0;

            var request = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = content,
                ContentType = contentType,
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
            return $"s3://{_bucketName}/{key}";
        }

        var fullPath = Path.Combine(_localRoot, key.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        content.Position = 0;
        await using var fileStream = File.Create(fullPath);
        await content.CopyToAsync(fileStream, ct);

        var uri = new Uri(fullPath).AbsoluteUri;
        _logger.LogDebug("Archive artifact written to local filesystem at {ArchiveUri}", uri);
        return uri;
    }

    public async Task<string> PutTextAsync(string key, string contentType, string content, CancellationToken ct)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await PutObjectAsync(key, contentType, stream, ct);
    }

    public void Dispose()
    {
        _s3Client?.Dispose();
    }
}
