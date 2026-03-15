using FccDesktopAgent.Api.Auth;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Peer;
using FccDesktopAgent.Core.PreAuth;
using FccDesktopAgent.Core.Replication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FccDesktopAgent.Api.Endpoints;

/// <summary>
/// Peer-to-peer HA endpoints exposed on the peer API port.
/// Route group: /peer. All endpoints are authenticated via <see cref="PeerHmacAuthFilter"/>.
/// </summary>
internal static class PeerEndpoints
{
    internal static IEndpointRouteBuilder MapPeerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/peer")
            .WithTags("Peer")
            .AddEndpointFilter<PeerHmacAuthFilter>();

        // GET /peer/health — local agent health for peer probing
        group.MapGet("/health", (IPeerCoordinator coordinator) =>
        {
            var response = coordinator.BuildHealthResponse();
            return Results.Ok(response);
        })
        .WithName("getPeerHealth");

        // POST /peer/heartbeat — receive heartbeat from a peer
        group.MapPost("/heartbeat", (PeerHeartbeatRequest request, IPeerCoordinator coordinator) =>
        {
            var response = coordinator.HandleIncomingHeartbeat(request);
            return Results.Ok(response);
        })
        .WithName("peerHeartbeat");

        // POST /peer/claim-leadership — receive leadership claim from a candidate
        group.MapPost("/claim-leadership", (PeerLeadershipClaimRequest request, IPeerCoordinator coordinator) =>
        {
            var response = coordinator.HandleLeadershipClaim(request);
            return Results.Ok(response);
        })
        .WithName("peerClaimLeadership");

        // GET /peer/bootstrap — full snapshot for standby bootstrap
        group.MapGet("/bootstrap", async (PeerSyncHandler syncHandler, CancellationToken ct) =>
        {
            var snapshot = await syncHandler.GenerateSnapshotAsync(ct);
            return Results.Ok(snapshot);
        })
        .WithName("peerBootstrap");

        // GET /peer/sync — delta sync for standby replication
        group.MapGet("/sync", async (HttpContext ctx, PeerSyncHandler syncHandler, CancellationToken ct) =>
        {
            var sinceSeqStr = ctx.Request.Query["since"].FirstOrDefault();
            var sinceSeq = long.TryParse(sinceSeqStr, out var parsed) ? parsed : 0L;
            var delta = await syncHandler.GenerateDeltaAsync(sinceSeq, 500, ct);
            return Results.Ok(delta);
        })
        .WithName("peerSync");

        // POST /peer/proxy/preauth — forward pre-auth to local handler
        group.MapPost("/proxy/preauth", async (
            PeerProxyPreAuthRequest request,
            IServiceScopeFactory scopeFactory,
            CancellationToken ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetService<IPreAuthHandler>();
            if (handler is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            try
            {
                var odooRequest = new OdooPreAuthRequest(
                    OdooOrderId: request.OdooOrderId,
                    SiteCode: string.Empty, // Filled from local config by handler
                    OdooPumpNumber: request.PumpNumber,
                    OdooNozzleNumber: request.NozzleNumber,
                    RequestedAmountMinorUnits: request.RequestedAmount,
                    UnitPriceMinorPerLitre: request.UnitPrice,
                    Currency: request.Currency,
                    VehicleNumber: request.VehicleNumber,
                    CustomerName: request.CustomerName,
                    CustomerTaxId: request.CustomerTaxId,
                    CustomerBusinessName: request.CustomerBusinessName,
                    AttendantId: request.AttendantId);

                var result = await handler.HandleAsync(odooRequest, ct);

                return Results.Ok(new PeerProxyPreAuthResponse
                {
                    Success = result.IsSuccess,
                    PreAuthId = result.RecordId,
                    FccCorrelationId = result.FccCorrelationId,
                    FccAuthorizationCode = result.FccAuthorizationCode,
                    FailureReason = result.ErrorDetail,
                    Status = result.Status?.ToString() ?? "UNKNOWN",
                });
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        })
        .WithName("peerProxyPreAuth");

        // GET /peer/proxy/pump-status — get pump status from local FCC adapter
        group.MapGet("/proxy/pump-status", async (
            HttpContext ctx,
            IPumpStatusService pumpStatusService,
            CancellationToken ct) =>
        {
            var pumpNumberStr = ctx.Request.Query["pump"].FirstOrDefault();
            int? pumpNumber = int.TryParse(pumpNumberStr, out var p) ? p : null;
            var result = await pumpStatusService.GetPumpStatusAsync(pumpNumber, ct);
            var pumps = result.Pumps.Select(pump => new PeerPumpStatus
            {
                PumpNumber = pump.PumpNumber,
                Status = pump.State.ToString(),
                CurrentNozzle = pump.NozzleNumber,
                CurrentProductCode = pump.ProductCode,
            }).ToList();

            return Results.Ok(new PeerProxyPumpStatusResponse { Pumps = pumps });
        })
        .WithName("peerProxyPumpStatus");

        return app;
    }
}
