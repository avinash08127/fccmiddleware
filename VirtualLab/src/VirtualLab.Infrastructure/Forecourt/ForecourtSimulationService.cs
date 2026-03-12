using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using VirtualLab.Application.Forecourt;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Domain.Profiles;
using VirtualLab.Infrastructure.FccProfiles;
using VirtualLab.Infrastructure.Persistence;

namespace VirtualLab.Infrastructure.Forecourt;

public sealed class ForecourtSimulationService(
    VirtualLabDbContext dbContext,
    CallbackDeliveryService callbackDeliveryService) : IForecourtSimulationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<NozzleActionResult> LiftAsync(
        Guid siteId,
        Guid pumpId,
        Guid nozzleId,
        NozzleLiftRequest request,
        CancellationToken cancellationToken = default)
    {
        NozzleSiteContext? context = await LoadNozzleContextAsync(siteId, pumpId, nozzleId, cancellationToken);
        if (context is null)
        {
            return NotFoundNozzleResult();
        }

        if (request.ForceFault)
        {
            return await FaultNozzleAsync(context, request.CorrelationId, request.FaultMessage, cancellationToken);
        }

        if (context.Nozzle.State is NozzleState.Lifted or NozzleState.Authorized or NozzleState.Dispensing)
        {
            return ConflictResult(context, "Nozzle is already active.");
        }

        if (context.Nozzle.State == NozzleState.Faulted)
        {
            return ConflictResult(context, "Nozzle is faulted. Clear the fault before lifting.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        PreAuthSession? activeSession = await LoadActivePreAuthSessionAsync(context, null, cancellationToken);
        NozzleSimulationState state = LoadSimulationState(context.Nozzle);
        state.CorrelationId = await ResolveCorrelationIdAsync(context, request.CorrelationId, activeSession?.CorrelationId, cancellationToken);
        state.FlowRateLitresPerMinute = state.FlowRateLitresPerMinute <= 0 ? 32m : state.FlowRateLitresPerMinute;
        state.PreAuthSessionId = activeSession?.Id;
        state.LiftedAtUtc = now;
        state.UpdatedAtUtc = now;
        AppendTimeline(
            state.Timeline,
            now,
            "NozzleLifted",
            activeSession is null ? "Lifted" : "Authorized",
            "Nozzle lifted from cradle.",
            new
            {
                context.Site.SiteCode,
                context.Pump.PumpNumber,
                context.Nozzle.NozzleNumber,
                preAuthSessionId = activeSession?.Id,
            });

        NozzleState previousState = context.Nozzle.State;
        context.Nozzle.State = activeSession is null ? NozzleState.Lifted : NozzleState.Authorized;
        context.Nozzle.SimulationStateJson = Serialize(state);
        context.Nozzle.UpdatedAtUtc = now;

        if (activeSession is not null)
        {
            AppendPreAuthTimeline(activeSession, now, "NozzleLifted", "AUTHORIZED", "Authorized nozzle lifted for dispensing.");
        }

        AddEventLog(
            context,
            state.CorrelationId,
            "LabAction",
            "NozzleLifted",
            $"Lifted nozzle {context.Nozzle.NozzleNumber} on pump {context.Pump.PumpNumber}.",
            metadata: new
            {
                previousState = previousState.ToString().ToUpperInvariant(),
                currentState = context.Nozzle.State.ToString().ToUpperInvariant(),
            });
        AddStateTransitionLog(context, state.CorrelationId, previousState, context.Nozzle.State, "Lift action applied.");

        await dbContext.SaveChangesAsync(cancellationToken);
        return SuccessResult(context, state.CorrelationId, "Nozzle lifted.");
    }

    public async Task<NozzleActionResult> HangAsync(
        Guid siteId,
        Guid pumpId,
        Guid nozzleId,
        NozzleHangRequest request,
        CancellationToken cancellationToken = default)
    {
        NozzleSiteContext? context = await LoadNozzleContextAsync(siteId, pumpId, nozzleId, cancellationToken);
        if (context is null)
        {
            return NotFoundNozzleResult();
        }

        NozzleSimulationState state = LoadSimulationState(context.Nozzle);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string correlationId = await ResolveCorrelationIdAsync(context, request.CorrelationId, state.CorrelationId, cancellationToken);

        if (context.Nozzle.State == NozzleState.Idle)
        {
            return ConflictResult(context, "Nozzle is already idle.", correlationId);
        }

        if (context.Nozzle.State == NozzleState.Faulted && !request.ClearFault)
        {
            return ConflictResult(context, "Nozzle is faulted. Set clearFault to hang and recover it.", correlationId);
        }

        PreAuthSession? activeSession = await LoadActivePreAuthSessionAsync(context, state.PreAuthSessionId, cancellationToken);

        if (context.Nozzle.State == NozzleState.Dispensing)
        {
            StopDispense(context, state, activeSession, request.ElapsedSeconds, now);
        }

        SimulatedTransaction? transaction = null;
        if (state.AccumulatedVolume > 0 && !state.TransactionGenerated)
        {
            AppendTimeline(
                state.Timeline,
                now,
                "NozzleHung",
                "Hung",
                "Nozzle returned to cradle and transaction finalized.",
                new
                {
                    volume = state.AccumulatedVolume,
                    amount = state.AccumulatedAmount,
                });

            transaction = await CreateTransactionAsync(context, state, activeSession, now, cancellationToken);
            state.TransactionGenerated = true;

            if (activeSession is not null)
            {
                activeSession.Status = PreAuthSessionStatus.Completed;
                activeSession.FinalAmount = transaction.TotalAmount;
                activeSession.FinalVolume = transaction.Volume;
                activeSession.CompletedAtUtc = now;
                AppendPreAuthTimeline(activeSession, now, "DispenseCompleted", "COMPLETED", "Dispense completed from nozzle simulation.");
            }
        }
        else
        {
            AppendTimeline(
                state.Timeline,
                now,
                "NozzleHung",
                "Hung",
                "Nozzle returned to cradle.",
                metadata: null);
        }

        NozzleState previousState = context.Nozzle.State;
        context.Nozzle.State = NozzleState.Hung;
        context.Nozzle.UpdatedAtUtc = now;
        context.Nozzle.SimulationStateJson = "{}";

        AddEventLog(
            context,
            correlationId,
            "LabAction",
            "NozzleHung",
            $"Hung nozzle {context.Nozzle.NozzleNumber} on pump {context.Pump.PumpNumber}.",
            metadata: new
            {
                previousState = previousState.ToString().ToUpperInvariant(),
                currentState = context.Nozzle.State.ToString().ToUpperInvariant(),
                transactionGenerated = transaction is not null,
            });
        AddStateTransitionLog(context, correlationId, previousState, context.Nozzle.State, "Hang action applied.");

        if (transaction is not null && context.Site.DeliveryMode is TransactionDeliveryMode.Push or TransactionDeliveryMode.Hybrid)
        {
            await PushTransactionsInternalAsync(
                context.Site,
                [transaction],
                requestedTargetKey: null,
                cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return SuccessResult(context, correlationId, "Nozzle hung.", transaction);
    }

    public async Task<NozzleActionResult> DispenseAsync(
        Guid siteId,
        Guid pumpId,
        Guid nozzleId,
        DispenseSimulationRequest request,
        CancellationToken cancellationToken = default)
    {
        NozzleSiteContext? context = await LoadNozzleContextAsync(siteId, pumpId, nozzleId, cancellationToken);
        if (context is null)
        {
            return NotFoundNozzleResult();
        }

        if (request.ForceFault)
        {
            return await FaultNozzleAsync(context, request.CorrelationId, request.FailureMessage, cancellationToken);
        }

        NozzleSimulationState state = LoadSimulationState(context.Nozzle);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PreAuthSession? activeSession = await LoadActivePreAuthSessionAsync(context, state.PreAuthSessionId, cancellationToken);
        string correlationId = await ResolveCorrelationIdAsync(context, request.CorrelationId, state.CorrelationId ?? activeSession?.CorrelationId, cancellationToken);
        string action = string.IsNullOrWhiteSpace(request.Action) ? "start" : request.Action.Trim().ToLowerInvariant();

        if (context.Nozzle.State == NozzleState.Faulted)
        {
            return ConflictResult(context, "Nozzle is faulted. Clear the fault before dispensing.", correlationId);
        }

        return action switch
        {
            "start" => await StartDispenseAsync(context, state, activeSession, request, correlationId, now, cancellationToken),
            "stop" => await StopDispenseAsync(context, state, activeSession, request, correlationId, now, cancellationToken),
            _ => new NozzleActionResult(
                StatusCodes.Status400BadRequest,
                $"Unsupported dispense action '{request.Action}'. Use 'start' or 'stop'.",
                CreateSnapshot(context, correlationId),
                null,
                false,
                false,
                correlationId),
        };
    }

    public async Task<PushTransactionsResult> PushTransactionsAsync(
        Guid siteId,
        PushTransactionsRequest request,
        CancellationToken cancellationToken = default)
    {
        Site? site = await dbContext.Sites
            .SingleOrDefaultAsync(x => x.Id == siteId && x.IsActive, cancellationToken);

        if (site is null)
        {
            return new PushTransactionsResult(StatusCodes.Status404NotFound, "Site was not found.", 0, []);
        }

        if (site.DeliveryMode == TransactionDeliveryMode.Pull)
        {
            return new PushTransactionsResult(StatusCodes.Status409Conflict, "Site is configured for pull delivery only.", 0, []);
        }

        IQueryable<SimulatedTransaction> query = dbContext.SimulatedTransactions
            .Where(x => x.SiteId == siteId && x.Status != SimulatedTransactionStatus.Acknowledged);

        if (request.TransactionIds is { Count: > 0 })
        {
            query = query.Where(x => request.TransactionIds.Contains(x.ExternalTransactionId));
        }

        List<SimulatedTransaction> transactions = await query
            .OrderBy(x => x.OccurredAtUtc)
            .ToListAsync(cancellationToken);

        IReadOnlyList<PushTransactionAttemptSummary> attempts = await PushTransactionsInternalAsync(
            site,
            transactions,
            request.TargetKey,
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new PushTransactionsResult(
            StatusCodes.Status200OK,
            attempts.Count == 0 ? "No transactions were eligible for push delivery." : "Push delivery simulated.",
            attempts.Count(x => string.Equals(x.Status, "Succeeded", StringComparison.OrdinalIgnoreCase)),
            attempts);
    }

    public async Task<FccEndpointResult> GetPumpStatusAsync(string siteCode, CancellationToken cancellationToken = default)
    {
        Site? site = await dbContext.Sites
            .AsNoTracking()
            .Include(x => x.Pumps)
                .ThenInclude(x => x.Nozzles)
                    .ThenInclude(x => x.Product)
            .SingleOrDefaultAsync(x => x.SiteCode == siteCode && x.IsActive, cancellationToken);

        if (site is null)
        {
            return CreateFccJsonResponse(StatusCodes.Status404NotFound, new { message = $"Site '{siteCode}' was not found." });
        }

        return CreateFccJsonResponse(StatusCodes.Status200OK, new
        {
            status = "ok",
            site.SiteCode,
            deliveryMode = site.DeliveryMode.ToString().ToUpperInvariant(),
            occurredAtUtc = DateTimeOffset.UtcNow,
            pumps = site.Pumps
                .OrderBy(x => x.PumpNumber)
                .Select(pump => new
                {
                    pump.PumpNumber,
                    pump.Label,
                    state = ResolvePumpState(pump.Nozzles),
                    nozzles = pump.Nozzles
                        .OrderBy(x => x.NozzleNumber)
                        .Select(nozzle => new
                        {
                            nozzle.NozzleNumber,
                            nozzle.Label,
                            state = nozzle.State.ToString().ToUpperInvariant(),
                            productCode = nozzle.Product.ProductCode,
                            productName = nozzle.Product.Name,
                            unitPrice = nozzle.Product.UnitPrice,
                            updatedAtUtc = nozzle.UpdatedAtUtc,
                        }),
                }),
        });
    }

    public async Task<FccEndpointResult> GetHealthAsync(string siteCode, CancellationToken cancellationToken = default)
    {
        Site? site = await dbContext.Sites
            .AsNoTracking()
            .Include(x => x.ActiveFccSimulatorProfile)
            .SingleOrDefaultAsync(x => x.SiteCode == siteCode && x.IsActive, cancellationToken);

        if (site is null)
        {
            return CreateFccJsonResponse(StatusCodes.Status404NotFound, new { message = $"Site '{siteCode}' was not found." });
        }

        int pendingCallbacks = await dbContext.CallbackAttempts.CountAsync(
            x =>
                x.SimulatedTransaction.SiteId == site.Id &&
                (x.Status == CallbackAttemptStatus.Pending || x.Status == CallbackAttemptStatus.InProgress),
            cancellationToken);
        int failedCallbacks = await dbContext.CallbackAttempts.CountAsync(
            x =>
                x.SimulatedTransaction.SiteId == site.Id &&
                x.Status == CallbackAttemptStatus.Failed,
            cancellationToken);

        return CreateFccJsonResponse(StatusCodes.Status200OK, new
        {
            status = "ok",
            site.SiteCode,
            profileKey = site.ActiveFccSimulatorProfile.ProfileKey,
            deliveryMode = site.DeliveryMode.ToString().ToUpperInvariant(),
            preAuthMode = site.PreAuthMode.ToString().ToUpperInvariant(),
            pendingCallbacks,
            failedCallbacks,
            occurredAtUtc = DateTimeOffset.UtcNow,
        });
    }

    public async Task<PullTransactionsResult> PullTransactionsAsync(
        string siteCode,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        Site? site = await dbContext.Sites
            .Include(x => x.ActiveFccSimulatorProfile)
            .SingleOrDefaultAsync(x => x.SiteCode == siteCode && x.IsActive, cancellationToken);

        if (site is null)
        {
            return CreatePullJsonResponse(StatusCodes.Status404NotFound, new { message = $"Site '{siteCode}' was not found." });
        }

        FccProfileContract contract = FccProfileService.ToRecord(site.ActiveFccSimulatorProfile).Contract;
        bool pullEnabled = contract.Capabilities.SupportsPull || site.DeliveryMode is TransactionDeliveryMode.Pull or TransactionDeliveryMode.Hybrid;
        if (!pullEnabled)
        {
            return CreatePullJsonResponse(StatusCodes.Status404NotFound, new { message = $"Pull delivery is not enabled for site '{siteCode}'." });
        }

        int take = Math.Clamp(limit, 1, 100);
        List<SimulatedTransaction> candidates = await dbContext.SimulatedTransactions
            .Where(x => x.SiteId == site.Id && x.Status != SimulatedTransactionStatus.Acknowledged)
            .OrderBy(x => x.DeliveryCursor)
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            candidates = candidates
                .Where(x => string.CompareOrdinal(x.DeliveryCursor, cursor) > 0)
                .ToList();
        }

        List<SimulatedTransaction> batch = candidates.Take(take).ToList();
        List<JsonElement> payloads = [];
        string? nextCursor = batch.LastOrDefault()?.DeliveryCursor;

        foreach (SimulatedTransaction transaction in batch)
        {
            TransactionSimulationMetadata metadata = LoadTransactionMetadata(transaction);
            metadata.PullDeliveryCount++;

            if (transaction.Status == SimulatedTransactionStatus.ReadyForDelivery)
            {
                transaction.Status = SimulatedTransactionStatus.Delivered;
                transaction.DeliveredAtUtc ??= DateTimeOffset.UtcNow;
            }

            AppendTransactionTimeline(
                transaction,
                DateTimeOffset.UtcNow,
                "TransactionPulled",
                "Delivered",
                "Transaction included in pull batch.",
                new
                {
                    deliveryCursor = transaction.DeliveryCursor,
                    pullDeliveryCount = metadata.PullDeliveryCount,
                });
            AddEventLog(
                site,
                transaction,
                "TransactionPulled",
                "TransactionPulled",
                "Transaction returned through pull delivery.",
                metadata: new
                {
                    transaction.ExternalTransactionId,
                    transaction.DeliveryCursor,
                    duplicateInjected = false,
                });

            payloads.Add(ParseJsonElement(transaction.RawPayloadJson));

            if (metadata.DuplicateInjectionEnabled && metadata.PullDuplicateEmissionCount == 0)
            {
                metadata.PullDuplicateEmissionCount++;
                payloads.Add(ParseJsonElement(transaction.RawPayloadJson));
                AddEventLog(
                    site,
                    transaction,
                    "TransactionPulled",
                    "DuplicatePullInjected",
                    "Duplicate pull payload injected for testing.",
                    metadata: new
                    {
                        transaction.ExternalTransactionId,
                        duplicateInjected = true,
                    });
                AppendTransactionTimeline(
                    transaction,
                    DateTimeOffset.UtcNow,
                    "DuplicatePullInjected",
                    "Delivered",
                    "Duplicate pull payload injected.",
                    new
                    {
                        duplicateEmissionCount = metadata.PullDuplicateEmissionCount,
                    });
            }

            transaction.MetadataJson = Serialize(metadata);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatePullJsonResponse(StatusCodes.Status200OK, new
        {
            siteCode = site.SiteCode,
            deliveryMode = site.DeliveryMode.ToString().ToUpperInvariant(),
            cursor = nextCursor,
            transactions = payloads,
        });
    }

    public async Task<AcknowledgeTransactionsResult> AcknowledgeTransactionsAsync(
        string siteCode,
        AcknowledgeTransactionsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.TransactionIds.Count == 0)
        {
            return CreateAckJsonResponse(StatusCodes.Status400BadRequest, new { message = "At least one transaction id is required." });
        }

        Site? site = await dbContext.Sites
            .SingleOrDefaultAsync(x => x.SiteCode == siteCode && x.IsActive, cancellationToken);

        if (site is null)
        {
            return CreateAckJsonResponse(StatusCodes.Status404NotFound, new { message = $"Site '{siteCode}' was not found." });
        }

        string correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? $"ack-{Guid.NewGuid():N}"[..16]
            : request.CorrelationId.Trim();

        List<SimulatedTransaction> transactions = await dbContext.SimulatedTransactions
            .Where(x => x.SiteId == site.Id && request.TransactionIds.Contains(x.ExternalTransactionId))
            .ToListAsync(cancellationToken);

        foreach (SimulatedTransaction transaction in transactions)
        {
            transaction.Status = SimulatedTransactionStatus.Acknowledged;
            AppendTransactionTimeline(
                transaction,
                DateTimeOffset.UtcNow,
                "TransactionAcknowledged",
                "Acknowledged",
                "Transaction acknowledged by pull consumer.",
                new
                {
                    acknowledgementCorrelationId = correlationId,
                });
            AddEventLog(
                site,
                transaction,
                "TransactionPulled",
                "TransactionAcknowledged",
                "Transaction acknowledged by pull consumer.",
                correlationId,
                new
                {
                    transaction.ExternalTransactionId,
                });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return CreateAckJsonResponse(StatusCodes.Status200OK, new
        {
            siteCode = site.SiteCode,
            acknowledged = transactions.Count,
            transactionIds = transactions.Select(x => x.ExternalTransactionId).ToArray(),
            correlationId,
        });
    }

    private async Task<NozzleActionResult> StartDispenseAsync(
        NozzleSiteContext context,
        NozzleSimulationState state,
        PreAuthSession? activeSession,
        DispenseSimulationRequest request,
        string correlationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (context.Nozzle.State is not (NozzleState.Lifted or NozzleState.Authorized))
        {
            return ConflictResult(context, "Nozzle must be lifted before dispense can start.", correlationId);
        }

        state.CorrelationId = correlationId;
        state.PreAuthSessionId = activeSession?.Id;
        state.FlowRateLitresPerMinute = request.FlowRateLitresPerMinute ?? state.FlowRateLitresPerMinute;
        if (state.FlowRateLitresPerMinute <= 0)
        {
            state.FlowRateLitresPerMinute = 32m;
        }

        state.TargetAmount = request.TargetAmount ?? state.TargetAmount;
        state.TargetVolume = request.TargetVolume ?? state.TargetVolume;
        state.DuplicateInjectionEnabled = request.InjectDuplicate || state.DuplicateInjectionEnabled;
        state.SimulateFailureEnabled = request.SimulateFailure || state.SimulateFailureEnabled;
        state.FailureMessage = !string.IsNullOrWhiteSpace(request.FailureMessage)
            ? request.FailureMessage.Trim()
            : state.FailureMessage;
        state.DispenseStartedAtUtc = now;
        state.UpdatedAtUtc = now;

        AppendTimeline(
            state.Timeline,
            now,
            "DispenseStarted",
            "Dispensing",
            "Dispense started.",
            new
            {
                flowRateLitresPerMinute = state.FlowRateLitresPerMinute,
                targetAmount = state.TargetAmount,
                targetVolume = state.TargetVolume,
                injectDuplicate = state.DuplicateInjectionEnabled,
                simulateFailure = state.SimulateFailureEnabled,
            });

        NozzleState previousState = context.Nozzle.State;
        context.Nozzle.State = NozzleState.Dispensing;
        context.Nozzle.SimulationStateJson = Serialize(state);
        context.Nozzle.UpdatedAtUtc = now;

        if (activeSession is not null && activeSession.Status == PreAuthSessionStatus.Authorized)
        {
            activeSession.Status = PreAuthSessionStatus.Dispensing;
            AppendPreAuthTimeline(activeSession, now, "DispenseStarted", "DISPENSING", "Pre-auth nozzle entered dispensing.");
        }

        AddEventLog(
            context,
            correlationId,
            "LabAction",
            "DispenseStarted",
            "Dispense started on nozzle.",
            metadata: new
            {
                previousState = previousState.ToString().ToUpperInvariant(),
                currentState = context.Nozzle.State.ToString().ToUpperInvariant(),
                state.TargetAmount,
                state.TargetVolume,
            });
        AddStateTransitionLog(context, correlationId, previousState, context.Nozzle.State, "Dispense start applied.");

        await dbContext.SaveChangesAsync(cancellationToken);
        return SuccessResult(context, correlationId, "Dispense started.");
    }

    private async Task<NozzleActionResult> StopDispenseAsync(
        NozzleSiteContext context,
        NozzleSimulationState state,
        PreAuthSession? activeSession,
        DispenseSimulationRequest request,
        string correlationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (context.Nozzle.State != NozzleState.Dispensing)
        {
            return ConflictResult(context, "Nozzle is not currently dispensing.", correlationId);
        }

        if (request.FlowRateLitresPerMinute.HasValue && request.FlowRateLitresPerMinute > 0)
        {
            state.FlowRateLitresPerMinute = request.FlowRateLitresPerMinute.Value;
        }

        if (request.TargetAmount.HasValue)
        {
            state.TargetAmount = request.TargetAmount;
        }

        if (request.TargetVolume.HasValue)
        {
            state.TargetVolume = request.TargetVolume;
        }

        state.DuplicateInjectionEnabled = request.InjectDuplicate || state.DuplicateInjectionEnabled;
        state.SimulateFailureEnabled = request.SimulateFailure || state.SimulateFailureEnabled;
        state.FailureMessage = !string.IsNullOrWhiteSpace(request.FailureMessage)
            ? request.FailureMessage.Trim()
            : state.FailureMessage;

        StopDispense(context, state, activeSession, request.ElapsedSeconds, now);
        context.Nozzle.SimulationStateJson = Serialize(state);

        AddEventLog(
            context,
            correlationId,
            "LabAction",
            "DispenseStopped",
            "Dispense stopped on nozzle.",
            metadata: new
            {
                state.AccumulatedVolume,
                state.AccumulatedAmount,
            });

        await dbContext.SaveChangesAsync(cancellationToken);
        return SuccessResult(context, correlationId, "Dispense stopped.");
    }

    private void StopDispense(
        NozzleSiteContext context,
        NozzleSimulationState state,
        PreAuthSession? activeSession,
        int? elapsedSeconds,
        DateTimeOffset now)
    {
        int effectiveSeconds = Math.Max(
            elapsedSeconds ?? ResolveElapsedSeconds(state.DispenseStartedAtUtc, now),
            1);
        decimal flowRate = state.FlowRateLitresPerMinute <= 0 ? 32m : state.FlowRateLitresPerMinute;
        decimal volumeToAdd = RoundVolume(flowRate * effectiveSeconds / 60m);
        decimal? maxVolume = ResolveMaximumVolume(state.TargetVolume, state.TargetAmount, context.Product.UnitPrice);

        decimal newVolume = state.AccumulatedVolume + volumeToAdd;
        if (maxVolume.HasValue && newVolume > maxVolume.Value)
        {
            newVolume = maxVolume.Value;
        }

        state.AccumulatedVolume = RoundVolume(newVolume);
        state.AccumulatedAmount = RoundAmount(state.AccumulatedVolume * context.Product.UnitPrice);
        state.TotalDispenseSeconds += effectiveSeconds;
        state.HasDispensed = state.AccumulatedVolume > 0;
        state.DispenseStartedAtUtc = null;
        state.LastStoppedAtUtc = now;
        state.UpdatedAtUtc = now;

        NozzleState previousState = context.Nozzle.State;
        context.Nozzle.State = activeSession is null ? NozzleState.Lifted : NozzleState.Authorized;
        context.Nozzle.UpdatedAtUtc = now;

        AppendTimeline(
            state.Timeline,
            now,
            "DispenseStopped",
            context.Nozzle.State.ToString(),
            "Dispense stopped.",
            new
            {
                elapsedSeconds = effectiveSeconds,
                accumulatedVolume = state.AccumulatedVolume,
                accumulatedAmount = state.AccumulatedAmount,
            });
        AddStateTransitionLog(context, state.CorrelationId, previousState, context.Nozzle.State, "Dispense stop applied.");
    }

    private async Task<NozzleActionResult> FaultNozzleAsync(
        NozzleSiteContext context,
        string? requestedCorrelationId,
        string? faultMessage,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NozzleSimulationState state = LoadSimulationState(context.Nozzle);
        string correlationId = await ResolveCorrelationIdAsync(context, requestedCorrelationId, state.CorrelationId, cancellationToken);
        NozzleState previousState = context.Nozzle.State;

        state.CorrelationId = correlationId;
        state.UpdatedAtUtc = now;
        AppendTimeline(
            state.Timeline,
            now,
            "NozzleFaulted",
            "Faulted",
            string.IsNullOrWhiteSpace(faultMessage) ? "Nozzle transitioned to faulted state." : faultMessage.Trim(),
            metadata: null);

        context.Nozzle.State = NozzleState.Faulted;
        context.Nozzle.SimulationStateJson = Serialize(state);
        context.Nozzle.UpdatedAtUtc = now;

        AddEventLog(
            context,
            correlationId,
            "LabAction",
            "NozzleFaulted",
            string.IsNullOrWhiteSpace(faultMessage) ? "Nozzle fault injected." : faultMessage.Trim(),
            metadata: new
            {
                previousState = previousState.ToString().ToUpperInvariant(),
                currentState = context.Nozzle.State.ToString().ToUpperInvariant(),
            });
        AddStateTransitionLog(context, correlationId, previousState, context.Nozzle.State, "Fault injected on nozzle.");

        await dbContext.SaveChangesAsync(cancellationToken);
        return new NozzleActionResult(
            StatusCodes.Status200OK,
            "Nozzle faulted.",
            CreateSnapshot(context, correlationId),
            null,
            false,
            true,
            correlationId);
    }

    private async Task<SimulatedTransaction> CreateTransactionAsync(
        NozzleSiteContext context,
        NozzleSimulationState state,
        PreAuthSession? activeSession,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        int sequence = await dbContext.SimulatedTransactions
            .CountAsync(x => x.NozzleId == context.Nozzle.Id, cancellationToken) + 1;
        int seed = context.Environment.DeterministicSeed;
        Guid transactionId = seed == 0
            ? Guid.NewGuid()
            : CreateDeterministicGuid($"transaction:{seed}:{context.Site.SiteCode}:{context.Pump.PumpNumber}:{context.Nozzle.NozzleNumber}:{sequence}");
        string externalTransactionId = seed == 0
            ? $"TX-{Guid.NewGuid():N}"[..11].ToUpperInvariant()
            : $"TX-{seed:D6}-{context.Pump.PumpNumber:D2}{context.Nozzle.NozzleNumber:D2}-{sequence:D4}";
        string correlationId = state.CorrelationId;
        string occurredAtText = now.ToString("O");
        string deliveryCursor = $"{now.UtcTicks:D20}:{externalTransactionId}";

        string rawPayloadJson = Serialize(new
        {
            transactionId = externalTransactionId,
            correlationId,
            siteCode = context.Site.SiteCode,
            pumpNumber = context.Pump.PumpNumber,
            nozzleNumber = context.Nozzle.NozzleNumber,
            productCode = context.Product.ProductCode,
            productName = context.Product.Name,
            volume = state.AccumulatedVolume,
            amount = state.AccumulatedAmount,
            unitPrice = context.Product.UnitPrice,
            currencyCode = context.Product.CurrencyCode,
            occurredAtUtc = occurredAtText,
            preAuthId = activeSession?.ExternalReference,
        });

        string canonicalPayloadJson = Serialize(new
        {
            fccTransactionId = externalTransactionId,
            correlationId,
            siteCode = context.Site.SiteCode,
            pumpNumber = context.Pump.PumpNumber,
            nozzleNumber = context.Nozzle.NozzleNumber,
            productCode = context.Product.ProductCode,
            actualVolume = state.AccumulatedVolume,
            actualAmount = state.AccumulatedAmount,
            unitPrice = context.Product.UnitPrice,
            currencyCode = context.Product.CurrencyCode,
            source = "VirtualLab",
        });

        AppendTimeline(
            state.Timeline,
            now,
            "TransactionGenerated",
            "ReadyForDelivery",
            "Simulated transaction generated from nozzle activity.",
            new
            {
                externalTransactionId,
                deliveryMode = context.Site.DeliveryMode.ToString().ToUpperInvariant(),
            });

        TransactionSimulationMetadata metadata = new()
        {
            DuplicateInjectionEnabled = state.DuplicateInjectionEnabled,
            SimulateFailureEnabled = state.SimulateFailureEnabled,
            FailureMessage = string.IsNullOrWhiteSpace(state.FailureMessage) ? "Injected delivery failure." : state.FailureMessage,
            FlowRateLitresPerMinute = state.FlowRateLitresPerMinute,
            TargetAmount = state.TargetAmount,
            TargetVolume = state.TargetVolume,
            TotalDispenseSeconds = state.TotalDispenseSeconds,
        };

        SimulatedTransaction transaction = new()
        {
            Id = transactionId,
            SiteId = context.Site.Id,
            PumpId = context.Pump.Id,
            NozzleId = context.Nozzle.Id,
            ProductId = context.Product.Id,
            PreAuthSessionId = activeSession?.Id,
            CorrelationId = correlationId,
            ExternalTransactionId = externalTransactionId,
            DeliveryMode = context.Site.DeliveryMode,
            Status = SimulatedTransactionStatus.ReadyForDelivery,
            Volume = state.AccumulatedVolume,
            UnitPrice = context.Product.UnitPrice,
            TotalAmount = state.AccumulatedAmount,
            OccurredAtUtc = now,
            CreatedAtUtc = now,
            RawPayloadJson = rawPayloadJson,
            CanonicalPayloadJson = canonicalPayloadJson,
            RawHeadersJson = """{"content-type":"application/json"}""",
            DeliveryCursor = deliveryCursor,
            MetadataJson = Serialize(metadata),
            TimelineJson = Serialize(state.Timeline),
        };

        dbContext.SimulatedTransactions.Add(transaction);
        AddEventLog(
            context.Site,
            transaction,
            "TransactionGenerated",
            "TransactionCreated",
            "Simulated transaction generated from nozzle activity.",
            correlationId,
            new
            {
                context.Site.SiteCode,
                context.Pump.PumpNumber,
                context.Nozzle.NozzleNumber,
                context.Product.ProductCode,
                transaction.Volume,
                transaction.TotalAmount,
            });

        return transaction;
    }

    private Task<IReadOnlyList<PushTransactionAttemptSummary>> PushTransactionsInternalAsync(
        Site site,
        IReadOnlyList<SimulatedTransaction> transactions,
        string? requestedTargetKey,
        CancellationToken cancellationToken)
    {
        return callbackDeliveryService.QueueAndDispatchAsync(site, transactions, requestedTargetKey, cancellationToken);
    }

    private async Task<NozzleSiteContext?> LoadNozzleContextAsync(Guid siteId, Guid pumpId, Guid nozzleId, CancellationToken cancellationToken)
    {
        return await dbContext.Nozzles
            .Include(x => x.Pump)
                .ThenInclude(x => x.Site)
                    .ThenInclude(x => x.LabEnvironment)
            .Include(x => x.Pump)
                .ThenInclude(x => x.Site)
                    .ThenInclude(x => x.ActiveFccSimulatorProfile)
            .Include(x => x.Product)
            .Where(x => x.Id == nozzleId && x.PumpId == pumpId && x.Pump.SiteId == siteId && x.IsActive)
            .Select(x => new NozzleSiteContext(
                x.Pump.Site.LabEnvironment,
                x.Pump.Site,
                x.Pump,
                x,
                x.Product))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<PreAuthSession?> LoadActivePreAuthSessionAsync(
        NozzleSiteContext context,
        Guid? preferredSessionId,
        CancellationToken cancellationToken)
    {
        IQueryable<PreAuthSession> query = dbContext.PreAuthSessions
            .Where(x =>
                x.SiteId == context.Site.Id &&
                x.PumpId == context.Pump.Id &&
                x.NozzleId == context.Nozzle.Id &&
                (x.Status == PreAuthSessionStatus.Authorized || x.Status == PreAuthSessionStatus.Dispensing));

        if (preferredSessionId.HasValue)
        {
            PreAuthSession? preferred = await query
                .OrderByDescending(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(x => x.Id == preferredSessionId.Value, cancellationToken);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<string> ResolveCorrelationIdAsync(
        NozzleSiteContext context,
        string? requestedCorrelationId,
        string? existingCorrelationId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedCorrelationId))
        {
            return requestedCorrelationId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(existingCorrelationId))
        {
            return existingCorrelationId.Trim();
        }

        int sequence = await dbContext.SimulatedTransactions
            .CountAsync(x => x.NozzleId == context.Nozzle.Id, cancellationToken) + 1;

        if (context.Environment.DeterministicSeed != 0)
        {
            return $"corr-{context.Environment.DeterministicSeed:D6}-{context.Pump.PumpNumber:D2}{context.Nozzle.NozzleNumber:D2}-{sequence:D4}";
        }

        return $"corr-{Guid.NewGuid():N}"[..18];
    }

    private static void AppendTimeline(
        IList<TimelineEntry> timeline,
        DateTimeOffset atUtc,
        string eventType,
        string state,
        string message,
        object? metadata)
    {
        timeline.Add(new TimelineEntry
        {
            Event = eventType,
            State = state,
            Message = message,
            AtUtc = atUtc,
            Metadata = metadata,
        });
    }

    private static void AppendTransactionTimeline(
        SimulatedTransaction transaction,
        DateTimeOffset atUtc,
        string eventType,
        string state,
        string message,
        object? metadata)
    {
        List<TimelineEntry> timeline = Deserialize<List<TimelineEntry>>(transaction.TimelineJson, []);
        AppendTimeline(timeline, atUtc, eventType, state, message, metadata);
        transaction.TimelineJson = Serialize(timeline);
    }

    private static void AppendPreAuthTimeline(PreAuthSession session, DateTimeOffset atUtc, string eventType, string state, string message)
    {
        List<TimelineEntry> timeline = Deserialize<List<TimelineEntry>>(session.TimelineJson, []);
        AppendTimeline(timeline, atUtc, eventType, state, message, metadata: null);
        session.TimelineJson = Serialize(timeline);
    }

    private void AddStateTransitionLog(NozzleSiteContext context, string correlationId, NozzleState previousState, NozzleState currentState, string message)
    {
        AddEventLog(
            context,
            correlationId,
            "StateTransition",
            "NozzleStateChanged",
            message,
            metadata: new
            {
                previousState = previousState.ToString().ToUpperInvariant(),
                currentState = currentState.ToString().ToUpperInvariant(),
            });
    }

    private void AddEventLog(
        NozzleSiteContext context,
        string correlationId,
        string category,
        string eventType,
        string message,
        object? metadata)
    {
        dbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            SiteId = context.Site.Id,
            FccSimulatorProfileId = context.Site.ActiveFccSimulatorProfileId,
            CorrelationId = correlationId,
            Severity = "Information",
            Category = category,
            EventType = eventType,
            Message = message,
            RawPayloadJson = context.Nozzle.SimulationStateJson,
            CanonicalPayloadJson = "{}",
            MetadataJson = Serialize(metadata ?? new { }),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });
    }

    private void AddEventLog(
        Site site,
        SimulatedTransaction transaction,
        string category,
        string eventType,
        string message,
        string? correlationId = null,
        object? metadata = null)
    {
        dbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            FccSimulatorProfileId = site.ActiveFccSimulatorProfileId,
            PreAuthSessionId = transaction.PreAuthSessionId,
            SimulatedTransactionId = transaction.Id,
            CorrelationId = correlationId ?? transaction.CorrelationId,
            Severity = "Information",
            Category = category,
            EventType = eventType,
            Message = message,
            RawPayloadJson = transaction.RawPayloadJson,
            CanonicalPayloadJson = transaction.CanonicalPayloadJson,
            MetadataJson = Serialize(metadata ?? new { }),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });
    }

    private static NozzleSimulationState LoadSimulationState(Nozzle nozzle)
    {
        NozzleSimulationState state = Deserialize<NozzleSimulationState>(nozzle.SimulationStateJson, new NozzleSimulationState());
        state.Timeline ??= [];
        return state;
    }

    private static TransactionSimulationMetadata LoadTransactionMetadata(SimulatedTransaction transaction)
    {
        return Deserialize<TransactionSimulationMetadata>(transaction.MetadataJson, new TransactionSimulationMetadata());
    }

    private static SiteSettings LoadSiteSettings(string json)
    {
        return Deserialize<SiteSettings>(json, new SiteSettings());
    }

    private static NozzleActionResult SuccessResult(
        NozzleSiteContext context,
        string correlationId,
        string message,
        SimulatedTransaction? transaction = null)
    {
        return new NozzleActionResult(
            StatusCodes.Status200OK,
            message,
            CreateSnapshot(context, correlationId),
            transaction is null ? null : CreateTransactionSummary(transaction),
            transaction is not null,
            context.Nozzle.State == NozzleState.Faulted,
            correlationId);
    }

    private static NozzleActionResult ConflictResult(NozzleSiteContext context, string message, string? correlationId = null)
    {
        string resolvedCorrelationId = correlationId ?? string.Empty;
        return new NozzleActionResult(
            StatusCodes.Status409Conflict,
            message,
            CreateSnapshot(context, resolvedCorrelationId),
            null,
            false,
            context.Nozzle.State == NozzleState.Faulted,
            resolvedCorrelationId);
    }

    private static NozzleActionResult NotFoundNozzleResult()
    {
        return new NozzleActionResult(
            StatusCodes.Status404NotFound,
            "Nozzle was not found for the specified site and pump.",
            null,
            null,
            false,
            false,
            string.Empty);
    }

    private static NozzleSimulationSnapshot CreateSnapshot(NozzleSiteContext context, string correlationId)
    {
        NozzleSimulationState state = LoadSimulationState(context.Nozzle);
        string effectiveCorrelationId = !string.IsNullOrWhiteSpace(correlationId) ? correlationId : state.CorrelationId;
        return new NozzleSimulationSnapshot(
            context.Site.Id,
            context.Pump.Id,
            context.Nozzle.Id,
            context.Site.SiteCode,
            context.Pump.PumpNumber,
            context.Nozzle.NozzleNumber,
            context.Nozzle.Label,
            context.Nozzle.State,
            context.Product.ProductCode,
            context.Product.Name,
            context.Product.UnitPrice,
            context.Product.CurrencyCode,
            effectiveCorrelationId,
            state.PreAuthSessionId,
            context.Nozzle.SimulationStateJson,
            context.Nozzle.UpdatedAtUtc);
    }

    private static TransactionSimulationSummary CreateTransactionSummary(SimulatedTransaction transaction)
    {
        return new TransactionSimulationSummary(
            transaction.Id,
            transaction.ExternalTransactionId,
            transaction.CorrelationId,
            transaction.DeliveryMode,
            transaction.Status,
            transaction.Volume,
            transaction.TotalAmount,
            transaction.UnitPrice,
            transaction.OccurredAtUtc,
            transaction.RawPayloadJson,
            transaction.CanonicalPayloadJson,
            transaction.MetadataJson,
            transaction.TimelineJson);
    }

    private static int ResolveElapsedSeconds(DateTimeOffset? startedAtUtc, DateTimeOffset now)
    {
        if (!startedAtUtc.HasValue)
        {
            return 1;
        }

        return (int)Math.Max(1, Math.Round((now - startedAtUtc.Value).TotalSeconds, MidpointRounding.AwayFromZero));
    }

    private static decimal? ResolveMaximumVolume(decimal? targetVolume, decimal? targetAmount, decimal unitPrice)
    {
        decimal? amountVolume = targetAmount.HasValue && unitPrice > 0
            ? RoundVolume(targetAmount.Value / unitPrice)
            : null;

        if (targetVolume.HasValue && amountVolume.HasValue)
        {
            return Math.Min(targetVolume.Value, amountVolume.Value);
        }

        return targetVolume ?? amountVolume;
    }

    private static decimal RoundVolume(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static decimal RoundAmount(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static JsonElement ParseJsonElement(string json)
    {
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return document.RootElement.Clone();
    }

    private static Guid CreateDeterministicGuid(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] hash = SHA256.HashData(bytes);
        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    private static AcknowledgeTransactionsResult CreateAckJsonResponse(int statusCode, object payload)
    {
        return new AcknowledgeTransactionsResult(statusCode, "application/json", Serialize(payload));
    }

    private static PullTransactionsResult CreatePullJsonResponse(int statusCode, object payload)
    {
        return new PullTransactionsResult(statusCode, "application/json", Serialize(payload));
    }

    private static FccEndpointResult CreateFccJsonResponse(int statusCode, object payload)
    {
        return new FccEndpointResult(statusCode, "application/json", Serialize(payload));
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static T Deserialize<T>(string json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private sealed record NozzleSiteContext(
        LabEnvironment Environment,
        Site Site,
        Pump Pump,
        Nozzle Nozzle,
        Product Product);

    private sealed class NozzleSimulationState
    {
        public string CorrelationId { get; set; } = string.Empty;
        public Guid? PreAuthSessionId { get; set; }
        public decimal FlowRateLitresPerMinute { get; set; } = 32m;
        public decimal? TargetAmount { get; set; }
        public decimal? TargetVolume { get; set; }
        public decimal AccumulatedVolume { get; set; }
        public decimal AccumulatedAmount { get; set; }
        public int TotalDispenseSeconds { get; set; }
        public bool HasDispensed { get; set; }
        public bool TransactionGenerated { get; set; }
        public bool DuplicateInjectionEnabled { get; set; }
        public bool SimulateFailureEnabled { get; set; }
        public string FailureMessage { get; set; } = string.Empty;
        public DateTimeOffset? LiftedAtUtc { get; set; }
        public DateTimeOffset? DispenseStartedAtUtc { get; set; }
        public DateTimeOffset? LastStoppedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public List<TimelineEntry> Timeline { get; set; } = [];
    }

    private sealed class TransactionSimulationMetadata
    {
        public bool DuplicateInjectionEnabled { get; set; }
        public bool SimulateFailureEnabled { get; set; }
        public string FailureMessage { get; set; } = string.Empty;
        public decimal FlowRateLitresPerMinute { get; set; }
        public decimal? TargetAmount { get; set; }
        public decimal? TargetVolume { get; set; }
        public int TotalDispenseSeconds { get; set; }
        public int PullDeliveryCount { get; set; }
        public int PushDeliveryCount { get; set; }
        public int PullDuplicateEmissionCount { get; set; }
        public int PushDuplicateEmissionCount { get; set; }
        public int PushFailureCount { get; set; }
    }

    private sealed class TimelineEntry
    {
        public string Event { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTimeOffset AtUtc { get; set; }
        public object? Metadata { get; set; }
    }

    private sealed class SiteSettings
    {
        public string? DefaultCallbackTargetKey { get; set; }
    }

    private static string ResolvePumpState(IEnumerable<Nozzle> nozzles)
    {
        if (nozzles.Any(x => x.State == NozzleState.Faulted))
        {
            return "FAULTED";
        }

        if (nozzles.Any(x => x.State == NozzleState.Dispensing))
        {
            return "DISPENSING";
        }

        if (nozzles.Any(x => x.State == NozzleState.Authorized))
        {
            return "AUTHORIZED";
        }

        if (nozzles.Any(x => x.State == NozzleState.Lifted))
        {
            return "LIFTED";
        }

        return "IDLE";
    }
}
