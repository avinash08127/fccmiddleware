using System.Security.Claims;
using System.Text.Json;
using FccMiddleware.Api.Controllers;
using FccMiddleware.Api.Infrastructure;
using FccMiddleware.Application.Common;
using FccMiddleware.Application.Ingestion;
using FccMiddleware.Application.Observability;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Ingestion;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FccMiddleware.Api.Tests.Controllers;

public sealed class TransactionsControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ISiteFccConfigProvider _siteFccConfigProvider = Substitute.For<ISiteFccConfigProvider>();
    private readonly ILogger<TransactionsController> _logger = Substitute.For<ILogger<TransactionsController>>();
    private readonly IObservabilityMetrics _metrics = Substitute.For<IObservabilityMetrics>();
    private readonly IServiceScopeFactory _serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly IAuthoritativeWriteFenceService _writeFence = Substitute.For<IAuthoritativeWriteFenceService>();

    public TransactionsControllerTests()
    {
        _writeFence.ValidateAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AuthoritativeWriteFenceResult.Allow()));

        var services = new ServiceCollection();
        services.AddSingleton(_mediator);
        var provider = services.BuildServiceProvider();
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        _serviceScopeFactory.CreateScope().Returns(scope);
    }

    [Fact]
    public async Task Ingest_BulkWrappedPayload_ReturnsBatchResultsAndSendsEachTransaction()
    {
        _mediator.Send(Arg.Any<IngestTransactionCommand>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(Result<IngestTransactionResult>.Success(new IngestTransactionResult
                {
                    TransactionId = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                    IsDuplicate = false
                })),
                Task.FromResult(Result<IngestTransactionResult>.Success(new IngestTransactionResult
                {
                    TransactionId = Guid.Parse("10000000-0000-0000-0000-000000000002"),
                    IsDuplicate = false
                })));

        var controller = CreateController();
        var request = new IngestRequest
        {
            FccVendor = "DOMS",
            SiteCode = "ACCRA-001",
            CapturedAt = DateTimeOffset.Parse("2026-03-11T14:20:00Z"),
            RawPayload = ParseJson("""
                {
                  "transactions": [
                    {
                      "transactionId": "TX-1001",
                      "pumpNumber": 1,
                      "nozzleNumber": 1,
                      "productCode": "PMS",
                      "volumeMicrolitres": 1000000,
                      "amountMinorUnits": 1000,
                      "unitPriceMinorPerLitre": 1000,
                      "startTime": "2026-03-11T14:10:00Z",
                      "endTime": "2026-03-11T14:11:00Z"
                    },
                    {
                      "transactionId": "TX-1002",
                      "pumpNumber": 2,
                      "nozzleNumber": 1,
                      "productCode": "AGO",
                      "volumeMicrolitres": 2000000,
                      "amountMinorUnits": 2200,
                      "unitPriceMinorPerLitre": 1100,
                      "startTime": "2026-03-11T14:12:00Z",
                      "endTime": "2026-03-11T14:13:00Z"
                    }
                  ]
                }
                """)
        };

        var actionResult = await controller.Ingest(request, CancellationToken.None);

        var ok = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<IngestBatchResponse>().Subject;
        response.AcceptedCount.Should().Be(2);
        response.DuplicateCount.Should().Be(0);
        response.RejectedCount.Should().Be(0);
        response.Results.Should().HaveCount(2);
        response.Results[0].FccTransactionId.Should().Be("TX-1001");
        response.Results[0].Outcome.Should().Be("ACCEPTED");
        response.Results[1].FccTransactionId.Should().Be("TX-1002");
        response.Results[1].Outcome.Should().Be("ACCEPTED");

        await _mediator.Received(2).Send(
            Arg.Any<IngestTransactionCommand>(),
            Arg.Any<CancellationToken>());

        await _mediator.Received(1).Send(
            Arg.Is<IngestTransactionCommand>(cmd => cmd.RawPayload.Contains("TX-1001")),
            Arg.Any<CancellationToken>());

        await _mediator.Received(1).Send(
            Arg.Is<IngestTransactionCommand>(cmd => cmd.RawPayload.Contains("TX-1002")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_BulkWrappedPayload_WithRejectedRecord_ReturnsPerRecordOutcome()
    {
        _mediator.Send(Arg.Any<IngestTransactionCommand>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(Result<IngestTransactionResult>.Success(new IngestTransactionResult
                {
                    TransactionId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
                    IsDuplicate = false
                })),
                Task.FromResult(Result<IngestTransactionResult>.Failure("VALIDATION.MISSING_REQUIRED_FIELD", "transactionId is required")));

        var controller = CreateController();
        var request = new IngestRequest
        {
            FccVendor = "DOMS",
            SiteCode = "ACCRA-001",
            CapturedAt = DateTimeOffset.Parse("2026-03-11T14:25:00Z"),
            RawPayload = ParseJson("""
                {
                  "transactions": [
                    {
                      "transactionId": "TX-2001",
                      "pumpNumber": 1,
                      "nozzleNumber": 1,
                      "productCode": "PMS",
                      "volumeMicrolitres": 1000000,
                      "amountMinorUnits": 1000,
                      "unitPriceMinorPerLitre": 1000,
                      "startTime": "2026-03-11T14:10:00Z",
                      "endTime": "2026-03-11T14:11:00Z"
                    },
                    {
                      "pumpNumber": 2,
                      "nozzleNumber": 1,
                      "productCode": "AGO",
                      "volumeMicrolitres": 2000000,
                      "amountMinorUnits": 2200,
                      "unitPriceMinorPerLitre": 1100,
                      "startTime": "2026-03-11T14:12:00Z",
                      "endTime": "2026-03-11T14:13:00Z"
                    }
                  ]
                }
                """)
        };

        var actionResult = await controller.Ingest(request, CancellationToken.None);

        var ok = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<IngestBatchResponse>().Subject;
        response.AcceptedCount.Should().Be(1);
        response.DuplicateCount.Should().Be(0);
        response.RejectedCount.Should().Be(1);
        response.Results.Should().HaveCount(2);
        response.Results[0].Outcome.Should().Be("ACCEPTED");
        response.Results[1].Outcome.Should().Be("REJECTED");
        response.Results[1].ErrorCode.Should().Be("VALIDATION.MISSING_REQUIRED_FIELD");
        response.Results[1].FccTransactionId.Should().BeNull();
    }

    [Fact]
    public async Task Ingest_WithScopedSiteClaimMismatch_ReturnsForbidden_AndDoesNotSendCommand()
    {
        var controller = CreateController(
            new Claim("site", "SITE-ALLOWED"));

        var request = new IngestRequest
        {
            FccVendor = "DOMS",
            SiteCode = "SITE-BLOCKED",
            CapturedAt = DateTimeOffset.Parse("2026-03-11T14:25:00Z"),
            RawPayload = ParseJson("""{ "transactionId": "TX-3001" }""")
        };

        var actionResult = await controller.Ingest(request, CancellationToken.None);

        var forbidden = actionResult.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var error = forbidden.Value.Should().BeOfType<ErrorResponse>().Subject;
        error.ErrorCode.Should().Be("FORBIDDEN.SITE_SCOPE");

        await _mediator.DidNotReceive().Send(Arg.Any<IngestTransactionCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_WithScopedLeiClaimMismatch_ReturnsForbidden_AndDoesNotSendCommand()
    {
        var credentialLei = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var requestLei = Guid.Parse("40000000-0000-0000-0000-000000000002");

        _siteFccConfigProvider.GetBySiteCodeAsync("SITE-001", Arg.Any<CancellationToken>())
            .Returns((new Domain.Models.Adapter.SiteFccConfig
            {
                SiteCode = "SITE-001",
                FccVendor = FccVendor.DOMS,
                ConnectionProtocol = ConnectionProtocol.REST,
                HostAddress = "127.0.0.1",
                Port = 8080,
                ApiKey = string.Empty,
                IngestionMethod = IngestionMethod.PUSH,
                CurrencyCode = "GHS",
                Timezone = "UTC",
                PumpNumberOffset = 0,
                ProductCodeMapping = new Dictionary<string, string>()
            }, requestLei));

        var controller = CreateController(
            new Claim("lei", credentialLei.ToString()));

        var request = new IngestRequest
        {
            FccVendor = "DOMS",
            SiteCode = "SITE-001",
            CapturedAt = DateTimeOffset.Parse("2026-03-11T14:25:00Z"),
            RawPayload = ParseJson("""{ "transactionId": "TX-3002" }""")
        };

        var actionResult = await controller.Ingest(request, CancellationToken.None);

        var forbidden = actionResult.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var error = forbidden.Value.Should().BeOfType<ErrorResponse>().Subject;
        error.ErrorCode.Should().Be("FORBIDDEN.LEGAL_ENTITY_SCOPE");

        await _mediator.DidNotReceive().Send(Arg.Any<IngestTransactionCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_WhenWriteFenceRejects_ReturnsConflictAndSkipsMediator()
    {
        var deviceId = Guid.Parse("60000000-0000-0000-0000-000000000001");
        _writeFence.ValidateAsync(
                deviceId.ToString(),
                "SITE-A",
                4,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AuthoritativeWriteFenceResult.Reject(
                StatusCodes.Status409Conflict,
                "CONFLICT.STALE_LEADER_EPOCH",
                "stale epoch")));

        var controller = CreateController(
            new Claim(ClaimTypes.NameIdentifier, deviceId.ToString()),
            new Claim("site", "SITE-A"),
            new Claim("lei", Guid.Parse("60000000-0000-0000-0000-000000000002").ToString()));

        var request = new UploadRequest
        {
            LeaderEpoch = 4,
            Transactions =
            [
                new UploadTransactionRecord
                {
                    FccTransactionId = "UPLOAD-STALE-001",
                    SiteCode = "SITE-A",
                    FccVendor = "DOMS",
                    PumpNumber = 1,
                    NozzleNumber = 1,
                    ProductCode = "PMS",
                    VolumeMicrolitres = 1_000_000,
                    AmountMinorUnits = 1_000,
                    UnitPriceMinorPerLitre = 1_000,
                    CurrencyCode = "GHS",
                    StartedAt = DateTimeOffset.Parse("2026-03-11T14:10:00Z"),
                    CompletedAt = DateTimeOffset.Parse("2026-03-11T14:11:00Z")
                }
            ]
        };

        var actionResult = await controller.Upload(request, CancellationToken.None);

        var conflict = actionResult.Should().BeOfType<ConflictObjectResult>().Subject;
        var error = conflict.Value.Should().BeOfType<ErrorResponse>().Subject;
        error.ErrorCode.Should().Be("CONFLICT.STALE_LEADER_EPOCH");

        await _mediator.DidNotReceive().Send(Arg.Any<UploadTransactionBatchCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_ResponseIncludesPeerDirectoryVersionHeader_ViaMiddleware()
    {
        // The X-Peer-Directory-Version header is set by PeerDirectoryVersionMiddleware,
        // not by the controller itself. This test verifies the middleware adds the header
        // when a resolved agent and site with a peer directory version are present.
        var agent = new Domain.Entities.AgentRegistration
        {
            Id = Guid.Parse("70000000-0000-0000-0000-000000000001"),
            SiteId = Guid.Parse("70000000-0000-0000-0000-000000000002"),
            LegalEntityId = Guid.Parse("70000000-0000-0000-0000-000000000003"),
            SiteCode = "SITE-PDV",
            DeviceSerialNumber = "SER-PDV",
            DeviceModel = "DESKTOP",
            OsVersion = "test",
            AgentVersion = "1.0.0",
            DeviceClass = "DESKTOP",
            RoleCapability = "PRIMARY_ELIGIBLE",
            SiteHaPriority = 10,
            LeaderEpochSeen = 1,
            Status = Domain.Enums.AgentRegistrationStatus.ACTIVE,
            IsActive = true,
            RegisteredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var site = new Domain.Entities.Site
        {
            Id = agent.SiteId,
            SiteCode = "SITE-PDV",
            LegalEntityId = agent.LegalEntityId,
            PeerDirectoryVersion = 42,
        };

        var db = Substitute.For<Application.Registration.IRegistrationDbContext>();
        db.FindSiteBySiteCodeAsync("SITE-PDV", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Domain.Entities.Site?>(site));

        bool nextCalled = false;
        var middleware = new Infrastructure.PeerDirectoryVersionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var httpContext = new DefaultHttpContext();
        httpContext.Items[Infrastructure.DeviceActiveCheckMiddleware.ResolvedDeviceKey] = agent;

        await middleware.InvokeAsync(httpContext, db);

        nextCalled.Should().BeTrue();
        httpContext.Response.Headers.TryGetValue("X-Peer-Directory-Version", out var headerValues).Should().BeTrue();
        headerValues.ToString().Should().Be("42");
    }

    private TransactionsController CreateController(params Claim[] claims)
    {
        var controller = new TransactionsController(_mediator, _siteFccConfigProvider, _logger, _metrics, _serviceScopeFactory, _writeFence)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = claims.Length == 0
                        ? new ClaimsPrincipal(new ClaimsIdentity())
                        : new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };

        controller.HttpContext.Items["CorrelationId"] = Guid.Parse("30000000-0000-0000-0000-000000000001");
        return controller;
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
