using FccMiddleware.Application.DeadLetter;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Infrastructure.DeadLetter;
using FccMiddleware.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FccMiddleware.UnitTests.DeadLetter;

/// <summary>
/// Unit tests for <see cref="DlqReplayService"/> covering error paths,
/// state guards, and type dispatch logic.
/// Uses an EF Core InMemory provider to back <see cref="FccMiddlewareDbContext"/>.
/// </summary>
public sealed class DlqReplayServiceTests : IDisposable
{
    private readonly FccMiddlewareDbContext _db;
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<DlqReplayService> _logger = Substitute.For<ILogger<DlqReplayService>>();

    public DlqReplayServiceTests()
    {
        var tenantProvider = Substitute.For<ICurrentTenantProvider>();
        tenantProvider.CurrentLegalEntityId.Returns((Guid?)null); // no tenant filter

        var options = new DbContextOptionsBuilder<FccMiddlewareDbContext>()
            .UseInMemoryDatabase(databaseName: $"DlqReplayTests_{Guid.NewGuid()}")
            .Options;

        _db = new FccMiddlewareDbContext(options, tenantProvider);
    }

    private DlqReplayService CreateSut() => new(_db, _mediator, _logger);

    public void Dispose() => _db.Dispose();

    // -----------------------------------------------------------------------
    // Helper to seed a dead-letter item into the in-memory database
    // -----------------------------------------------------------------------
    private async Task<DeadLetterItem> SeedItemAsync(
        DeadLetterStatus status = DeadLetterStatus.PENDING,
        DeadLetterType type = DeadLetterType.TRANSACTION,
        string? rawPayloadJson = null)
    {
        var item = new DeadLetterItem
        {
            Id = Guid.NewGuid(),
            LegalEntityId = Guid.NewGuid(),
            SiteCode = "SITE-001",
            Type = type,
            FailureReason = DeadLetterReason.VALIDATION_FAILURE,
            ErrorCode = "TEST_ERR",
            ErrorMessage = "Test error for unit test",
            Status = status,
            RetryCount = status == DeadLetterStatus.PENDING ? 0 : 1,
            RawPayloadJson = rawPayloadJson,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.DeadLetterItems.Add(item);
        await _db.SaveChangesAsync();
        // Detach so the service re-loads from DB
        _db.ChangeTracker.Clear();
        return item;
    }

    // -----------------------------------------------------------------------
    // 1. ReplayAsync returns NOT_FOUND for missing items
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReplayAsync_MissingItem_ReturnsNotFound()
    {
        var sut = CreateSut();
        var nonExistentId = Guid.NewGuid();

        var result = await sut.ReplayAsync(nonExistentId);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
        result.ErrorMessage.Should().Contain("not found");
    }

    // -----------------------------------------------------------------------
    // 2. ReplayAsync returns INVALID_STATE for RESOLVED items
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReplayAsync_ResolvedItem_ReturnsInvalidState()
    {
        var item = await SeedItemAsync(
            status: DeadLetterStatus.RESOLVED,
            rawPayloadJson: """{"fccVendor":"DOMS","amount":100}""");
        var sut = CreateSut();

        var result = await sut.ReplayAsync(item.Id);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("INVALID_STATE");
        result.ErrorMessage.Should().Contain("RESOLVED");
    }

    // -----------------------------------------------------------------------
    // 3. ReplayAsync returns INVALID_STATE for DISCARDED items
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReplayAsync_DiscardedItem_ReturnsInvalidState()
    {
        var item = await SeedItemAsync(
            status: DeadLetterStatus.DISCARDED,
            rawPayloadJson: """{"fccVendor":"DOMS","amount":100}""");
        var sut = CreateSut();

        var result = await sut.ReplayAsync(item.Id);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("INVALID_STATE");
        result.ErrorMessage.Should().Contain("DISCARDED");
    }

    // -----------------------------------------------------------------------
    // 4. ReplayAsync returns UNSUPPORTED_TYPE for non-TRANSACTION/PRE_AUTH types
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReplayAsync_UnsupportedType_ReturnsUnsupportedType()
    {
        var item = await SeedItemAsync(
            type: DeadLetterType.TELEMETRY,
            rawPayloadJson: """{"some":"data"}""");
        var sut = CreateSut();

        var result = await sut.ReplayAsync(item.Id);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("UNSUPPORTED_TYPE");
        result.ErrorMessage.Should().Contain("TELEMETRY");
    }

    [Fact]
    public async Task ReplayAsync_UnknownType_ReturnsUnsupportedType()
    {
        var item = await SeedItemAsync(
            type: DeadLetterType.UNKNOWN,
            rawPayloadJson: """{"some":"data"}""");
        var sut = CreateSut();

        var result = await sut.ReplayAsync(item.Id);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("UNSUPPORTED_TYPE");
    }

    // -----------------------------------------------------------------------
    // 5. ReplayAsync returns NO_PAYLOAD when RawPayloadJson is null
    //    (TRANSACTION type)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReplayAsync_TransactionWithNullPayload_ReturnsNoPayload()
    {
        var item = await SeedItemAsync(
            type: DeadLetterType.TRANSACTION,
            rawPayloadJson: null);
        var sut = CreateSut();

        var result = await sut.ReplayAsync(item.Id);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("NO_PAYLOAD");
        result.ErrorMessage.Should().Contain("no stored raw payload");
    }

    [Fact]
    public async Task ReplayAsync_TransactionWithEmptyPayload_ReturnsNoPayload()
    {
        var item = await SeedItemAsync(
            type: DeadLetterType.TRANSACTION,
            rawPayloadJson: "   ");
        var sut = CreateSut();

        var result = await sut.ReplayAsync(item.Id);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("NO_PAYLOAD");
    }

    // -----------------------------------------------------------------------
    // PRE_AUTH type with null payload also returns NO_PAYLOAD
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReplayAsync_PreAuthWithNullPayload_ReturnsNoPayload()
    {
        var item = await SeedItemAsync(
            type: DeadLetterType.PRE_AUTH,
            rawPayloadJson: null);
        var sut = CreateSut();

        var result = await sut.ReplayAsync(item.Id);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("NO_PAYLOAD");
    }

    // -----------------------------------------------------------------------
    // Retrying transition: item status is set to RETRYING before dispatch
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReplayAsync_SetsStatusToRetrying_AndIncrementsRetryCount()
    {
        var item = await SeedItemAsync(
            type: DeadLetterType.TELEMETRY,  // will hit UNSUPPORTED_TYPE after transition
            rawPayloadJson: """{"data":"x"}""");
        var sut = CreateSut();

        await sut.ReplayAsync(item.Id);

        // Reload from DB to verify persisted state
        _db.ChangeTracker.Clear();
        var reloaded = await _db.DeadLetterItems
            .IgnoreQueryFilters()
            .FirstAsync(i => i.Id == item.Id);

        // UNSUPPORTED_TYPE is not success, so status ends at REPLAY_FAILED
        reloaded.Status.Should().Be(DeadLetterStatus.REPLAY_FAILED);
        reloaded.RetryCount.Should().Be(1);
        reloaded.LastRetryAt.Should().NotBeNull();
    }

    // -----------------------------------------------------------------------
    // REPLAY_FAILED item can be retried (transitions to RETRYING again)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReplayAsync_ReplayFailedItem_CanBeRetriedAgain()
    {
        var item = await SeedItemAsync(
            status: DeadLetterStatus.REPLAY_FAILED,
            type: DeadLetterType.TELEMETRY,
            rawPayloadJson: """{"data":"x"}""");
        var sut = CreateSut();

        var result = await sut.ReplayAsync(item.Id);

        // The service should not return INVALID_STATE for REPLAY_FAILED
        result.ErrorCode.Should().NotBe("INVALID_STATE");

        _db.ChangeTracker.Clear();
        var reloaded = await _db.DeadLetterItems
            .IgnoreQueryFilters()
            .FirstAsync(i => i.Id == item.Id);

        reloaded.RetryCount.Should().Be(2); // was 1 from seed, incremented to 2
    }

    // -----------------------------------------------------------------------
    // Retry history is persisted after replay
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReplayAsync_RecordsRetryHistory()
    {
        var item = await SeedItemAsync(
            type: DeadLetterType.TELEMETRY,
            rawPayloadJson: """{"data":"x"}""");
        var sut = CreateSut();

        await sut.ReplayAsync(item.Id);

        _db.ChangeTracker.Clear();
        var reloaded = await _db.DeadLetterItems
            .IgnoreQueryFilters()
            .FirstAsync(i => i.Id == item.Id);

        reloaded.RetryHistoryJson.Should().NotBeNullOrWhiteSpace();
        reloaded.RetryHistoryJson.Should().Contain("FAILED");
        reloaded.RetryHistoryJson.Should().Contain("UNSUPPORTED_TYPE");
    }

    // -----------------------------------------------------------------------
    // PENDING item with REPLAY_QUEUED status can be replayed
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReplayAsync_ReplayQueuedItem_Proceeds()
    {
        var item = await SeedItemAsync(
            status: DeadLetterStatus.REPLAY_QUEUED,
            type: DeadLetterType.TELEMETRY,
            rawPayloadJson: """{"data":"x"}""");
        var sut = CreateSut();

        var result = await sut.ReplayAsync(item.Id);

        // Should NOT be blocked as INVALID_STATE
        result.ErrorCode.Should().NotBe("INVALID_STATE");
        // TELEMETRY -> UNSUPPORTED_TYPE
        result.ErrorCode.Should().Be("UNSUPPORTED_TYPE");
    }
}
