using FccMiddleware.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FccMiddleware.Infrastructure.Tests.Storage;

public sealed class S3RawPayloadArchiverTests : IDisposable
{
    private readonly string _localRoot = Path.Combine(
        Path.GetTempPath(),
        "fccmiddleware-raw-payload-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ArchiveAsync_WithoutS3Bucket_WritesPayloadToLocalFilesystem()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:RawPayloadLocalRoot"] = _localRoot
            })
            .Build();

        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);

        using var archiver = new S3RawPayloadArchiver(configuration, environment, NullLogger<S3RawPayloadArchiver>.Instance);
        const string rawPayload = """{ "transactionId": "TX-123", "amountMinorUnits": 1000 }""";

        var payloadRef = await archiver.ArchiveAsync(
            "11000000-0000-0000-0000-000000000001",
            "SITE-001",
            "TX-123",
            rawPayload,
            CancellationToken.None);

        payloadRef.Should().NotBeNull();
        payloadRef.Should().StartWith("file://");

        var archivedPath = new Uri(payloadRef!).LocalPath;
        File.Exists(archivedPath).Should().BeTrue();
        archivedPath.Should().Contain(Path.Combine("raw-payloads", "11000000-0000-0000-0000-000000000001", "SITE-001"));

        var archivedPayload = await File.ReadAllTextAsync(archivedPath);
        archivedPayload.Should().Be(rawPayload);
    }

    public void Dispose()
    {
        if (Directory.Exists(_localRoot))
        {
            Directory.Delete(_localRoot, recursive: true);
        }
    }
}
