using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using FccDesktopAgent.Core.PreAuth;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using PreAuthEntity = FccDesktopAgent.Core.Buffer.Entities.PreAuthRecord;

namespace FccDesktopAgent.Core.Tests.PreAuth;

/// <summary>
/// Unit tests for <see cref="PreAuthHandler"/>.
/// Uses real in-memory SQLite so unique constraints and EF queries are exercised.
/// </summary>
public sealed class PreAuthHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentDbContext _db;
    private readonly IFccAdapterFactory _adapterFactory;
    private readonly IFccAdapter _adapter;
    private readonly IConnectivityMonitor _connectivity;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly IConfigManager _configManager;
    private SiteConfig? _currentSiteConfig;
    private readonly PreAuthHandler _handler;

    private static readonly AgentConfiguration DefaultConfig = new()
    {
        FccBaseUrl = "http://fcc-lan:8080",
        FccApiKey = "test-key",
        FccVendor = FccVendor.Doms,
        PreAuthTimeoutSeconds = 5,
        PreAuthExpiryMinutes = 5,
        SiteId = "SITE-A",
    };

    public PreAuthHandlerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentDbContext(options);
        _db.Database.EnsureCreated();

        _adapterFactory = Substitute.For<IFccAdapterFactory>();
        _adapter = Substitute.For<IFccAdapter>();
        _adapterFactory.Create(Arg.Any<FccVendor>(), Arg.Any<FccConnectionConfig>()).Returns(_adapter);

        _connectivity = Substitute.For<IConnectivityMonitor>();
        _connectivity.Current.Returns(new ConnectivitySnapshot(
            ConnectivityState.FullyOnline, IsInternetUp: true, IsFccUp: true,
            MeasuredAt: DateTimeOffset.UtcNow));

        _config = Options.Create(DefaultConfig);
        _configManager = Substitute.For<IConfigManager>();
        _configManager.CurrentSiteConfig.Returns(_ => _currentSiteConfig);
        _currentSiteConfig = MakeSiteConfig();

        _handler = new PreAuthHandler(
            _db, _connectivity, _config,
            NullLogger<PreAuthHandler>.Instance,
            _adapterFactory,
            _configManager);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OdooPreAuthRequest MakeRequest(
        string orderId = "ORDER-001",
        string siteCode = "SITE-A",
        int pump = 1,
        int nozzle = 1) =>
        new(
            OdooOrderId: orderId,
            SiteCode: siteCode,
            OdooPumpNumber: pump,
            OdooNozzleNumber: nozzle,
            RequestedAmountMinorUnits: 50_000,
            UnitPriceMinorPerLitre: 1_500,
            Currency: "ETB");

    private static SiteConfig MakeSiteConfig(
        string vendor = "DOMS",
        string connectionProtocol = "REST",
        string hostAddress = "fcc-lan",
        int port = 8080) =>
        new()
        {
            Identity = new SiteConfigIdentity
            {
                SiteCode = "SITE-A",
                LegalEntityId = "LE-001",
            },
            Site = new SiteConfigSite
            {
                Currency = "ETB",
                Timezone = "Africa/Addis_Ababa",
            },
            Fcc = new SiteConfigFcc
            {
                Enabled = true,
                Vendor = vendor,
                ConnectionProtocol = connectionProtocol,
                HostAddress = hostAddress,
                Port = port,
            },
            Mappings = new SiteConfigMappings
            {
                PumpNumberOffset = 0,
            },
        };

    private async Task SeedNozzleMappingAsync(
        string siteCode = "SITE-A",
        int odooPump = 1, int odooNozzle = 1,
        int fccPump = 10, int fccNozzle = 10,
        bool active = true)
    {
        _db.NozzleMappings.Add(new NozzleMapping
        {
            Id = Guid.NewGuid().ToString(),
            SiteCode = siteCode,
            OdooPumpNumber = odooPump,
            OdooNozzleNumber = odooNozzle,
            FccPumpNumber = fccPump,
            FccNozzleNumber = fccNozzle,
            ProductCode = "DIESEL",
            IsActive = active,
        });
        await _db.SaveChangesAsync();
    }

    private static PreAuthResult AcceptedResult(string correlationId = "FCC-CORR-001") =>
        new(Accepted: true,
            FccCorrelationId: correlationId,
            FccAuthorizationCode: "AUTH-001",
            ErrorCode: null,
            ErrorMessage: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5));

    private static PreAuthResult DeclinedResult(string errorCode = "PUMP_BUSY") =>
        new(Accepted: false,
            FccCorrelationId: null,
            FccAuthorizationCode: null,
            ErrorCode: errorCode,
            ErrorMessage: "Pump is busy");

    // ── HandleAsync: Dedup ────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_NonTerminalDuplicate_ReturnsExistingRecord()
    {
        await SeedNozzleMappingAsync();
        _adapter.SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(AcceptedResult());

        var request = MakeRequest();

        // First request — succeeds
        var first = await _handler.HandleAsync(request, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        first.Status.Should().Be(PreAuthStatus.Authorized);

        // Simulate status still Authorized (non-terminal)
        var record = await _db.PreAuths.SingleAsync();
        record.Status.Should().Be(PreAuthStatus.Authorized);

        // Second request with same orderId — should dedup and return same record
        var second = await _handler.HandleAsync(request, CancellationToken.None);
        second.IsSuccess.Should().BeTrue();
        second.RecordId.Should().Be(first.RecordId);

        // Adapter should only have been called once
        await _adapter.Received(1).SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(PreAuthStatus.Pending)]
    [InlineData(PreAuthStatus.Authorized)]
    [InlineData(PreAuthStatus.Dispensing)]
    public async Task HandleAsync_NonTerminalStatuses_ReturnsExistingWithoutFccCall(PreAuthStatus status)
    {
        await SeedNozzleMappingAsync();

        // Pre-seed a record in non-terminal status
        _db.PreAuths.Add(new PreAuthEntity
        {
            Id = Guid.NewGuid().ToString(),
            OdooOrderId = "ORDER-001",
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            RequestedAmount = 50_000,
            UnitPrice = 1_500,
            Currency = "ETB",
            Status = status,
            RequestedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        });
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(MakeRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be(status);
        await _adapter.DidNotReceive().SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(PreAuthStatus.Completed)]
    [InlineData(PreAuthStatus.Cancelled)]
    [InlineData(PreAuthStatus.Expired)]
    [InlineData(PreAuthStatus.Failed)]
    public async Task HandleAsync_TerminalStatus_AllowsNewRequest(PreAuthStatus terminalStatus)
    {
        await SeedNozzleMappingAsync();

        var oldId = Guid.NewGuid().ToString();
        _db.PreAuths.Add(new PreAuthEntity
        {
            Id = oldId,
            OdooOrderId = "ORDER-001",
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            RequestedAmount = 50_000,
            UnitPrice = 1_500,
            Currency = "ETB",
            Status = terminalStatus,
            RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-25),
        });
        await _db.SaveChangesAsync();

        _adapter.SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(AcceptedResult());

        var result = await _handler.HandleAsync(MakeRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be(PreAuthStatus.Authorized);
        // Terminal record is deleted and a new one is created with a fresh ID
        var count = await _db.PreAuths.CountAsync();
        count.Should().Be(1);
        var record = await _db.PreAuths.SingleAsync();
        record.Id.Should().NotBe(oldId);
    }

    // ── HandleAsync: Nozzle mapping ───────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_NozzleMappingNotFound_ReturnsMappingError()
    {
        // No nozzle mapping seeded
        var result = await _handler.HandleAsync(MakeRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PreAuthHandlerError.NozzleMappingNotFound);
        await _adapter.DidNotReceive().SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_InactiveNozzle_ReturnsNozzleInactiveError()
    {
        await SeedNozzleMappingAsync(active: false);

        var result = await _handler.HandleAsync(MakeRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PreAuthHandlerError.NozzleInactive);
    }

    // ── HandleAsync: Connectivity ─────────────────────────────────────────────

    [Theory]
    [InlineData(ConnectivityState.FccUnreachable, true, false)]
    [InlineData(ConnectivityState.FullyOffline, false, false)]
    public async Task HandleAsync_FccDown_RejectsFccUnreachable(
        ConnectivityState state, bool internetUp, bool fccUp)
    {
        await SeedNozzleMappingAsync();
        _connectivity.Current.Returns(new ConnectivitySnapshot(state, internetUp, fccUp, DateTimeOffset.UtcNow));

        var result = await _handler.HandleAsync(MakeRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PreAuthHandlerError.FccUnreachable);
        await _adapter.DidNotReceive().SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_InternetDownFccUp_SucceedsViaMo()
    {
        // Internet down but FCC up — pre-auth should still work (LAN only)
        await SeedNozzleMappingAsync();
        _connectivity.Current.Returns(new ConnectivitySnapshot(
            ConnectivityState.InternetDown, IsInternetUp: false, IsFccUp: true, MeasuredAt: DateTimeOffset.UtcNow));
        _adapter.SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(AcceptedResult());

        var result = await _handler.HandleAsync(MakeRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be(PreAuthStatus.Authorized);
    }

    // ── HandleAsync: Adapter not configured ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_AdapterFactoryNull_ReturnsAdapterNotConfigured()
    {
        await SeedNozzleMappingAsync();

        var handlerNoAdapter = new PreAuthHandler(
            _db, _connectivity, _config,
            NullLogger<PreAuthHandler>.Instance,
            adapterFactory: null,
            configManager: _configManager);

        var result = await handlerNoAdapter.HandleAsync(MakeRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PreAuthHandlerError.AdapterNotConfigured);
    }

    [Fact]
    public async Task HandleAsync_UsesConfiguredVendorFromSiteConfig()
    {
        _currentSiteConfig = MakeSiteConfig(vendor: "RADIX");
        await SeedNozzleMappingAsync();
        _adapter.SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(AcceptedResult());

        var result = await _handler.HandleAsync(MakeRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _adapterFactory.Received(1).Create(
            FccVendor.Radix,
            Arg.Is<FccConnectionConfig>(cfg =>
                cfg.SiteCode == "SITE-A"
                && cfg.BaseUrl == "http://fcc-lan:8080"
                && cfg.CurrencyCode == "ETB"
                && cfg.Timezone == "Africa/Addis_Ababa"));
    }

    [Fact]
    public async Task HandleAsync_UnsupportedVendor_FailsExplicitly()
    {
        _currentSiteConfig = MakeSiteConfig(vendor: "ADVATEC");
        await SeedNozzleMappingAsync();

        var result = await _handler.HandleAsync(MakeRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PreAuthHandlerError.UnsupportedVendor);
        await _adapter.DidNotReceive().SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>());
    }

    // ── HandleAsync: FCC responses ────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_FccAccepted_CreatesAuthorizedRecord()
    {
        await SeedNozzleMappingAsync(fccPump: 10, fccNozzle: 10);
        _adapter.SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(AcceptedResult("CORR-XYZ"));

        var result = await _handler.HandleAsync(MakeRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be(PreAuthStatus.Authorized);
        result.FccAuthorizationCode.Should().Be("AUTH-001");
        result.FccCorrelationId.Should().Be("CORR-XYZ");
        result.ExpiresAt.Should().NotBeNull();

        // Verify DB record
        var record = await _db.PreAuths.AsNoTracking().SingleAsync();
        record.Status.Should().Be(PreAuthStatus.Authorized);
        record.FccCorrelationId.Should().Be("CORR-XYZ");
        record.FccAuthorizationCode.Should().Be("AUTH-001");
        record.AuthorizedAt.Should().NotBeNull();
        record.IsCloudSynced.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_FccAccepted_TranslatesOdooToFccPumpNumbers()
    {
        // Odoo pump=2/nozzle=3 → FCC pump=20/nozzle=30
        await SeedNozzleMappingAsync(odooPump: 2, odooNozzle: 3, fccPump: 20, fccNozzle: 30);
        _adapter.SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(AcceptedResult());

        await _handler.HandleAsync(MakeRequest(pump: 2, nozzle: 3), CancellationToken.None);

        await _adapter.Received(1).SendPreAuthAsync(
            Arg.Is<PreAuthCommand>(c => c.FccPumpNumber == 20 && c.FccNozzleNumber == 30),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_FccDeclined_CreatesFailedRecord()
    {
        await SeedNozzleMappingAsync();
        _adapter.SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(DeclinedResult("PUMP_BUSY"));

        var result = await _handler.HandleAsync(MakeRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PreAuthHandlerError.FccDeclined);

        var record = await _db.PreAuths.AsNoTracking().SingleAsync();
        record.Status.Should().Be(PreAuthStatus.Failed);
        record.FailureReason.Should().Be("PUMP_BUSY");
        record.FailedAt.Should().NotBeNull();
        record.IsCloudSynced.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_FccAccepted_UsesExpiresAtFromFcc()
    {
        await SeedNozzleMappingAsync();
        var fccExpiry = DateTimeOffset.UtcNow.AddMinutes(10);
        var fccResult = new PreAuthResult(true, "CORR", "AUTH", null, null, ExpiresAt: fccExpiry);
        _adapter.SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(fccResult);

        var result = await _handler.HandleAsync(MakeRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ExpiresAt.Should().BeCloseTo(fccExpiry, TimeSpan.FromSeconds(1));

        var record = await _db.PreAuths.AsNoTracking().SingleAsync();
        record.ExpiresAt.Should().BeCloseTo(fccExpiry, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task HandleAsync_RecordMarkedForCloudSync()
    {
        await SeedNozzleMappingAsync();
        _adapter.SendPreAuthAsync(Arg.Any<PreAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(AcceptedResult());

        await _handler.HandleAsync(MakeRequest(), CancellationToken.None);

        var record = await _db.PreAuths.AsNoTracking().SingleAsync();
        record.IsCloudSynced.Should().BeFalse();
    }

    // ── CancelAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelAsync_PendingRecord_CancelsAndAttemptsFccDeauth()
    {
        await SeedNozzleMappingAsync();
        _db.PreAuths.Add(new PreAuthEntity
        {
            Id = "PA-001",
            OdooOrderId = "ORDER-001",
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            RequestedAmount = 50_000,
            UnitPrice = 1_500,
            Currency = "ETB",
            Status = PreAuthStatus.Pending,
            FccCorrelationId = "FCC-CORR-001",
            RequestedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        });
        await _db.SaveChangesAsync();
        _adapter.CancelPreAuthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _handler.CancelAsync("ORDER-001", "SITE-A", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be(PreAuthStatus.Cancelled);

        var record = await _db.PreAuths.AsNoTracking().SingleAsync();
        record.Status.Should().Be(PreAuthStatus.Cancelled);
        record.CancelledAt.Should().NotBeNull();
        record.IsCloudSynced.Should().BeFalse();

        await _adapter.Received(1).CancelPreAuthAsync("FCC-CORR-001", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelAsync_AuthorizedRecord_Cancels()
    {
        _db.PreAuths.Add(new PreAuthEntity
        {
            Id = "PA-002",
            OdooOrderId = "ORDER-002",
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            RequestedAmount = 50_000,
            UnitPrice = 1_500,
            Currency = "ETB",
            Status = PreAuthStatus.Authorized,
            FccCorrelationId = "FCC-CORR-002",
            RequestedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        });
        await _db.SaveChangesAsync();
        _adapter.CancelPreAuthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.CancelAsync("ORDER-002", "SITE-A", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be(PreAuthStatus.Cancelled);
    }

    [Fact]
    public async Task CancelAsync_DispensingRecord_RejectsWithError()
    {
        _db.PreAuths.Add(new PreAuthEntity
        {
            Id = "PA-003",
            OdooOrderId = "ORDER-003",
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            RequestedAmount = 50_000,
            UnitPrice = 1_500,
            Currency = "ETB",
            Status = PreAuthStatus.Dispensing,
            RequestedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        });
        await _db.SaveChangesAsync();

        var result = await _handler.CancelAsync("ORDER-003", "SITE-A", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PreAuthHandlerError.CannotCancelDispensing);

        var record = await _db.PreAuths.AsNoTracking().SingleAsync();
        record.Status.Should().Be(PreAuthStatus.Dispensing); // unchanged
    }

    [Fact]
    public async Task CancelAsync_RecordNotFound_ReturnsNotFoundError()
    {
        var result = await _handler.CancelAsync("NONEXISTENT", "SITE-A", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PreAuthHandlerError.RecordNotFound);
    }

    [Fact]
    public async Task CancelAsync_UsesConfiguredVendorFromSiteConfig()
    {
        _currentSiteConfig = MakeSiteConfig(vendor: "PETRONITE", hostAddress: "petronite-edge", port: 9443);
        _db.PreAuths.Add(new PreAuthEntity
        {
            Id = "PA-005",
            OdooOrderId = "ORDER-005",
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            RequestedAmount = 50_000,
            UnitPrice = 1_500,
            Currency = "ETB",
            Status = PreAuthStatus.Authorized,
            FccCorrelationId = "FCC-CORR-005",
            RequestedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        });
        await _db.SaveChangesAsync();

        var result = await _handler.CancelAsync("ORDER-005", "SITE-A", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _adapterFactory.Received(1).Create(
            FccVendor.Petronite,
            Arg.Is<FccConnectionConfig>(cfg =>
                cfg.SiteCode == "SITE-A"
                && cfg.BaseUrl == "http://petronite-edge:9443"));
        await _adapter.Received(1).CancelPreAuthAsync("FCC-CORR-005", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelAsync_NullCorrelationId_SkipsFccDeauth()
    {
        // Pending record with no FCC correlation ID (FCC call never reached)
        _db.PreAuths.Add(new PreAuthEntity
        {
            Id = "PA-004",
            OdooOrderId = "ORDER-004",
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            RequestedAmount = 50_000,
            UnitPrice = 1_500,
            Currency = "ETB",
            Status = PreAuthStatus.Pending,
            FccCorrelationId = null, // no correlation ID
            RequestedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        });
        await _db.SaveChangesAsync();

        var result = await _handler.CancelAsync("ORDER-004", "SITE-A", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be(PreAuthStatus.Cancelled);
        // Should NOT attempt FCC deauth when no correlation ID
        await _adapter.DidNotReceive().CancelPreAuthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── RunExpiryCheckAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task RunExpiryCheckAsync_ExpiredNonTerminalRecords_TransitionsToExpired()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-10);

        _db.PreAuths.AddRange(
            new PreAuthEntity
            {
                Id = "PA-EXP-1",
                OdooOrderId = "EXP-ORDER-1",
                SiteCode = "SITE-A",
                PumpNumber = 1,
                NozzleNumber = 1,
                ProductCode = "DIESEL",
                RequestedAmount = 50_000,
                UnitPrice = 1_500,
                Currency = "ETB",
                Status = PreAuthStatus.Authorized,
                ExpiresAt = past,
                RequestedAt = past,
            },
            new PreAuthEntity
            {
                Id = "PA-EXP-2",
                OdooOrderId = "EXP-ORDER-2",
                SiteCode = "SITE-A",
                PumpNumber = 2,
                NozzleNumber = 1,
                ProductCode = "DIESEL",
                RequestedAmount = 50_000,
                UnitPrice = 1_500,
                Currency = "ETB",
                Status = PreAuthStatus.Pending,
                ExpiresAt = past,
                RequestedAt = past,
            });
        await _db.SaveChangesAsync();

        var count = await _handler.RunExpiryCheckAsync(CancellationToken.None);

        count.Should().Be(2);

        var records = await _db.PreAuths.AsNoTracking().ToListAsync();
        records.Should().AllSatisfy(r =>
        {
            r.Status.Should().Be(PreAuthStatus.Expired);
            r.ExpiredAt.Should().NotBeNull();
            r.IsCloudSynced.Should().BeFalse();
        });
    }

    [Fact]
    public async Task RunExpiryCheckAsync_ActiveRecord_NotExpired()
    {
        _db.PreAuths.Add(new PreAuthEntity
        {
            Id = "PA-ACTIVE",
            OdooOrderId = "ACTIVE-ORDER",
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            RequestedAmount = 50_000,
            UnitPrice = 1_500,
            Currency = "ETB",
            Status = PreAuthStatus.Authorized,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), // not expired
            RequestedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        var count = await _handler.RunExpiryCheckAsync(CancellationToken.None);

        count.Should().Be(0);
        var record = await _db.PreAuths.AsNoTracking().SingleAsync();
        record.Status.Should().Be(PreAuthStatus.Authorized);
    }

    [Fact]
    public async Task RunExpiryCheckAsync_TerminalRecordPastExpiry_NotTransitioned()
    {
        // A completed record that happens to have a past ExpiresAt — should not be re-expired
        _db.PreAuths.Add(new PreAuthEntity
        {
            Id = "PA-DONE",
            OdooOrderId = "DONE-ORDER",
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            RequestedAmount = 50_000,
            UnitPrice = 1_500,
            Currency = "ETB",
            Status = PreAuthStatus.Completed,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-60),
        });
        await _db.SaveChangesAsync();

        var count = await _handler.RunExpiryCheckAsync(CancellationToken.None);

        count.Should().Be(0);
        var record = await _db.PreAuths.AsNoTracking().SingleAsync();
        record.Status.Should().Be(PreAuthStatus.Completed);
    }

    [Fact]
    public async Task RunExpiryCheckAsync_AttemptsFccDeauthForExpiredAuthorized()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-10);
        _db.PreAuths.Add(new PreAuthEntity
        {
            Id = "PA-EXPDEAUTH",
            OdooOrderId = "EXPDEAUTH-ORDER",
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            RequestedAmount = 50_000,
            UnitPrice = 1_500,
            Currency = "ETB",
            Status = PreAuthStatus.Authorized,
            FccCorrelationId = "FCC-CORR-EXP",
            ExpiresAt = past,
            RequestedAt = past,
        });
        await _db.SaveChangesAsync();
        _adapter.CancelPreAuthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        await _handler.RunExpiryCheckAsync(CancellationToken.None);

        await _adapter.Received(1).CancelPreAuthAsync("FCC-CORR-EXP", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunExpiryCheckAsync_FccDeauthFails_StillTransitionsToExpired()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-10);
        _db.PreAuths.Add(new PreAuthEntity
        {
            Id = "PA-EXPFAIL",
            OdooOrderId = "EXPFAIL-ORDER",
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            RequestedAmount = 50_000,
            UnitPrice = 1_500,
            Currency = "ETB",
            Status = PreAuthStatus.Authorized,
            FccCorrelationId = "FCC-CORR-FAIL",
            ExpiresAt = past,
            RequestedAt = past,
        });
        await _db.SaveChangesAsync();
        _adapter.CancelPreAuthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new HttpRequestException("FCC unreachable")));

        // Should not throw — best-effort deauth
        var count = await _handler.RunExpiryCheckAsync(CancellationToken.None);

        count.Should().Be(1);
        var record = await _db.PreAuths.AsNoTracking().SingleAsync();
        record.Status.Should().Be(PreAuthStatus.Expired);
    }

    [Fact]
    public async Task RunExpiryCheckAsync_EmptyDb_ReturnsZero()
    {
        var count = await _handler.RunExpiryCheckAsync(CancellationToken.None);
        count.Should().Be(0);
    }
}
