using System.Security.Claims;
using System.Text.Json;
using FccMiddleware.Api.Controllers;
using FccMiddleware.Application.Common;
using FccMiddleware.Application.Ingestion;
using FccMiddleware.Application.Observability;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Ingestion;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FccMiddleware.Api.Tests.Controllers;

public sealed class TransactionsControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ISiteFccConfigProvider _siteFccConfigProvider = Substitute.For<ISiteFccConfigProvider>();
    private readonly ILogger<TransactionsController> _logger = Substitute.For<ILogger<TransactionsController>>();
    private readonly IObservabilityMetrics _metrics = Substitute.For<IObservabilityMetrics>();

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

    private TransactionsController CreateController(params Claim[] claims)
    {
        var controller = new TransactionsController(_mediator, _siteFccConfigProvider, _logger, _metrics)
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
