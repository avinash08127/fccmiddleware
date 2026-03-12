using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using VirtualLab.Application.FccProfiles;
using VirtualLab.Application.PreAuth;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Domain.Profiles;
using VirtualLab.Infrastructure.FccProfiles;
using VirtualLab.Infrastructure.Persistence;

namespace VirtualLab.Infrastructure.PreAuth;

public sealed class PreAuthSimulationService(VirtualLabDbContext dbContext) : IPreAuthSimulationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<PreAuthSimulationResponse> HandleAsync(PreAuthSimulationRequest request, CancellationToken cancellationToken = default)
    {
        SiteProfileContext? context = await LoadContextAsync(request.SiteCode, cancellationToken);
        if (context is null)
        {
            return JsonResponse(StatusCodes.Status404NotFound, new { message = $"Site '{request.SiteCode}' was not found." });
        }

        await ExpireSiteSessionsAsync(context, cancellationToken);

        if (!IsOperationEnabled(context.Profile.Contract, request.Operation))
        {
            return JsonResponse(StatusCodes.Status404NotFound, new { message = $"Operation '{request.Operation}' is not enabled for site '{request.SiteCode}'." });
        }

        ParsedPreAuthRequest payload = ParseRequest(request);

        return request.Operation switch
        {
            "preauth-create" => await HandleCreateAsync(context, request, payload, cancellationToken),
            "preauth-authorize" => await HandleAuthorizeAsync(context, request, payload, cancellationToken),
            "preauth-cancel" => await HandleCancelAsync(context, request, payload, cancellationToken),
            _ => JsonResponse(StatusCodes.Status404NotFound, new { message = $"Unsupported operation '{request.Operation}'." }),
        };
    }

    public async Task<IReadOnlyList<PreAuthSessionSummary>> ListSessionsAsync(
        string? siteCode,
        string? correlationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        int take = Math.Clamp(limit, 1, 200);

        IQueryable<PreAuthSession> query = dbContext.PreAuthSessions
            .AsNoTracking()
            .Include(x => x.Site)
            .ThenInclude(x => x.ActiveFccSimulatorProfile)
            .OrderByDescending(x => x.CreatedAtUtc);

        if (!string.IsNullOrWhiteSpace(siteCode))
        {
            query = query.Where(x => x.Site.SiteCode == siteCode);
        }

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        List<PreAuthSession> sessions = await query
            .Take(take)
            .ToListAsync(cancellationToken);

        return sessions
            .Select(x => new PreAuthSessionSummary(
                x.Id,
                x.Site.SiteCode,
                x.Site.ActiveFccSimulatorProfile.ProfileKey,
                x.CorrelationId,
                x.ExternalReference,
                ToModeName(x.Mode),
                ToStatusName(x.Status),
                x.ReservedAmount,
                x.AuthorizedAmount,
                x.FinalAmount,
                x.FinalVolume,
                x.CreatedAtUtc,
                x.AuthorizedAtUtc,
                x.CompletedAtUtc,
                x.ExpiresAtUtc,
                x.RawRequestJson,
                x.CanonicalRequestJson,
                x.RawResponseJson,
                x.CanonicalResponseJson,
                x.TimelineJson))
            .ToList();
    }

    public async Task<int> ExpireSessionsAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<PreAuthSession> sessions = await dbContext.PreAuthSessions
            .Include(x => x.Site)
            .Where(x =>
                x.ExpiresAtUtc.HasValue &&
                x.ExpiresAtUtc <= now &&
                (x.Status == PreAuthSessionStatus.Pending ||
                 x.Status == PreAuthSessionStatus.Authorized ||
                 x.Status == PreAuthSessionStatus.Dispensing))
            .ToListAsync(cancellationToken);

        if (sessions.Count == 0)
        {
            return 0;
        }

        foreach (PreAuthSession session in sessions)
        {
            ExpireSession(session, session.Site.ActiveFccSimulatorProfileId, now, "Session expired before the pre-auth flow completed.");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return sessions.Count;
    }

    private async Task<PreAuthSimulationResponse> HandleCreateAsync(
        SiteProfileContext context,
        PreAuthSimulationRequest request,
        ParsedPreAuthRequest payload,
        CancellationToken cancellationToken)
    {
        List<string> validationErrors = [];

        if (payload.PumpNumber is null)
        {
            validationErrors.Add("pump is required.");
        }

        if (payload.NozzleNumber is null)
        {
            validationErrors.Add("nozzle is required.");
        }

        if (payload.Amount is null || payload.Amount <= 0)
        {
            validationErrors.Add("amount must be greater than zero.");
        }

        PumpNozzleMatch? match = null;

        if (validationErrors.Count == 0)
        {
            match = await LoadPumpNozzleMatchAsync(context.Site.Id, payload.PumpNumber!.Value, payload.NozzleNumber!.Value, cancellationToken);
            if (match is null)
            {
                validationErrors.Add("pump/nozzle combination was not found for the site.");
            }
        }

        string preAuthId = string.IsNullOrWhiteSpace(payload.PreAuthId)
            ? $"PA-{Guid.NewGuid():N}"[..11].ToUpperInvariant()
            : payload.PreAuthId;
        string correlationId = ResolveCorrelationId(payload, request);
        string canonicalRequest = Serialize(new
        {
            siteCode = context.Site.SiteCode,
            pumpNumber = payload.PumpNumber,
            nozzleNumber = payload.NozzleNumber,
            amount = payload.Amount,
            correlationId,
            preAuthId,
        });

        if (validationErrors.Count > 0)
        {
            await LogRequestAsync(context, null, correlationId, request, canonicalRequest);
            await LogValidationFailureAsync(context, null, correlationId, request.Operation, validationErrors, request.RawRequestJson);
            PreAuthSimulationResponse validationResponse = JsonResponse(
                StatusCodes.Status400BadRequest,
                new
                {
                    message = "Pre-auth create validation failed.",
                    errors = validationErrors,
                });
            await LogResponseAsync(context, null, correlationId, request.Operation, validationResponse.StatusCode, validationResponse.Body, request.RawRequestJson);
            await dbContext.SaveChangesAsync(cancellationToken);
            return validationResponse;
        }

        bool duplicateExists = await dbContext.PreAuthSessions
            .AnyAsync(x => x.SiteId == context.Site.Id && x.ExternalReference == preAuthId, cancellationToken);

        if (duplicateExists)
        {
            await LogRequestAsync(context, null, correlationId, request, canonicalRequest);
            string message = $"Pre-auth id '{preAuthId}' already exists for site '{context.Site.SiteCode}'.";
            await LogSequenceRejectedAsync(context, null, correlationId, request.Operation, message, request.RawRequestJson);
            PreAuthSimulationResponse duplicateResponse = JsonResponse(StatusCodes.Status409Conflict, new { message });
            await LogResponseAsync(context, null, correlationId, request.Operation, duplicateResponse.StatusCode, duplicateResponse.Body, request.RawRequestJson);
            await dbContext.SaveChangesAsync(cancellationToken);
            return duplicateResponse;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int expirySeconds = ResolveExpirySeconds(context.Site, context.Profile.Contract, payload);
        PreAuthSession session = new()
        {
            Id = Guid.NewGuid(),
            SiteId = context.Site.Id,
            PumpId = match!.PumpId,
            NozzleId = match.NozzleId,
            CorrelationId = correlationId,
            ExternalReference = preAuthId,
            Mode = context.Profile.Contract.PreAuthMode,
            Status = PreAuthSessionStatus.Pending,
            ReservedAmount = payload.Amount!.Value,
            RawRequestJson = NormalizeJson(request.RawRequestJson),
            CanonicalRequestJson = canonicalRequest,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(expirySeconds),
        };

        List<PreAuthTimelineEntry> timeline = [];
        AppendTimeline(
            timeline,
            now,
            request.Operation,
            "RequestReceived",
            "Captured FCC pre-auth create request.",
            metadata: new
            {
                request.Method,
                request.Path,
            });
        AppendTransition(context, session, timeline, null, PreAuthSessionStatus.Pending, now, request.Operation, "Created pre-auth session in PENDING state.");

        dbContext.PreAuthSessions.Add(session);
        await LogRequestAsync(context, session, correlationId, request, canonicalRequest);

        if (ShouldFail(context.Profile.Contract, payload, request.Operation, correlationId))
        {
            PreAuthSimulationResponse failureResponse = await FailSessionAsync(
                context,
                session,
                timeline,
                request,
                payload,
                cancellationToken);
            return failureResponse;
        }

        if (context.Profile.Contract.FailureSimulation.SimulatedDelayMs > 0)
        {
            await Task.Delay(context.Profile.Contract.FailureSimulation.SimulatedDelayMs, cancellationToken);
        }

        if (context.Profile.Contract.PreAuthMode == PreAuthFlowMode.CreateOnly)
        {
            session.AuthorizedAmount = session.ReservedAmount;
            AppendTransition(context, session, timeline, PreAuthSessionStatus.Pending, PreAuthSessionStatus.Authorized, now, request.Operation, "Create-only profile auto-authorized the session.");
        }

        string responseStatus = ToResponseStatus(session.Status);
        Dictionary<string, string> sampleValues = BuildResponseValues(context, session, request.Operation, responseStatus);
        string responseBody = RenderResponseBody(context.Profile.Contract, request.Operation, sampleValues);
        session.RawResponseJson = responseBody;
        session.CanonicalResponseJson = Serialize(new
        {
            status = ToStatusName(session.Status),
            preAuthId = session.ExternalReference,
            correlationId = session.CorrelationId,
            expiresAtUtc = session.ExpiresAtUtc,
        });

        AppendTimeline(
            timeline,
            DateTimeOffset.UtcNow,
            request.Operation,
            "ResponseSent",
            $"Returned {responseStatus} response for pre-auth create.",
            metadata: new
            {
                status = responseStatus,
                session.ExpiresAtUtc,
            });
        session.TimelineJson = Serialize(timeline);

        PreAuthSimulationResponse response = new(StatusCodes.Status200OK, "application/json", responseBody);
        await LogResponseAsync(context, session, correlationId, request.Operation, response.StatusCode, response.Body, request.RawRequestJson);
        await dbContext.SaveChangesAsync(cancellationToken);
        return response;
    }

    private async Task<PreAuthSimulationResponse> HandleAuthorizeAsync(
        SiteProfileContext context,
        PreAuthSimulationRequest request,
        ParsedPreAuthRequest payload,
        CancellationToken cancellationToken)
    {
        string correlationId = ResolveCorrelationId(payload, request);
        string canonicalRequest = Serialize(new
        {
            siteCode = context.Site.SiteCode,
            preAuthId = payload.PreAuthId,
            amount = payload.Amount,
            correlationId,
        });

        await LogRequestAsync(context, null, correlationId, request, canonicalRequest);

        List<string> validationErrors = [];
        if (string.IsNullOrWhiteSpace(payload.PreAuthId))
        {
            validationErrors.Add("preauthId is required.");
        }

        if (payload.Amount.HasValue && payload.Amount <= 0)
        {
            validationErrors.Add("amount must be greater than zero when provided.");
        }

        if (validationErrors.Count > 0)
        {
            await LogValidationFailureAsync(context, null, correlationId, request.Operation, validationErrors, request.RawRequestJson);
            PreAuthSimulationResponse validationResponse = JsonResponse(
                StatusCodes.Status400BadRequest,
                new
                {
                    message = "Pre-auth authorize validation failed.",
                    errors = validationErrors,
                });
            await LogResponseAsync(context, null, correlationId, request.Operation, validationResponse.StatusCode, validationResponse.Body, request.RawRequestJson);
            await dbContext.SaveChangesAsync(cancellationToken);
            return validationResponse;
        }

        PreAuthSession? session = await dbContext.PreAuthSessions
            .SingleOrDefaultAsync(
                x => x.SiteId == context.Site.Id && x.ExternalReference == payload.PreAuthId,
                cancellationToken);

        if (session is null)
        {
            string message = $"Pre-auth id '{payload.PreAuthId}' was not found.";
            await LogSequenceRejectedAsync(context, null, correlationId, request.Operation, message, request.RawRequestJson);
            PreAuthSimulationResponse notFoundResponse = JsonResponse(StatusCodes.Status404NotFound, new { message });
            await LogResponseAsync(context, null, correlationId, request.Operation, notFoundResponse.StatusCode, notFoundResponse.Body, request.RawRequestJson);
            await dbContext.SaveChangesAsync(cancellationToken);
            return notFoundResponse;
        }

        if (await TryExpireSessionAsync(context, session, cancellationToken))
        {
            string message = $"Pre-auth id '{payload.PreAuthId}' already expired.";
            await LogSequenceRejectedAsync(context, session, correlationId, request.Operation, message, request.RawRequestJson);
            PreAuthSimulationResponse expiredResponse = JsonResponse(StatusCodes.Status409Conflict, new { message, status = ToStatusName(session.Status) });
            await LogResponseAsync(context, session, correlationId, request.Operation, expiredResponse.StatusCode, expiredResponse.Body, request.RawRequestJson);
            await dbContext.SaveChangesAsync(cancellationToken);
            return expiredResponse;
        }

        if (context.Profile.Contract.PreAuthMode != PreAuthFlowMode.CreateThenAuthorize)
        {
            string message = $"Profile '{context.Profile.ProfileKey}' does not require a separate authorize step.";
            await LogSequenceRejectedAsync(context, session, correlationId, request.Operation, message, request.RawRequestJson);
            PreAuthSimulationResponse modeConflictResponse = JsonResponse(StatusCodes.Status409Conflict, new { message });
            await LogResponseAsync(context, session, correlationId, request.Operation, modeConflictResponse.StatusCode, modeConflictResponse.Body, request.RawRequestJson);
            await dbContext.SaveChangesAsync(cancellationToken);
            return modeConflictResponse;
        }

        if (session.Status != PreAuthSessionStatus.Pending)
        {
            string message = $"Cannot authorize session '{session.ExternalReference}' from state '{ToStatusName(session.Status)}'.";
            await LogSequenceRejectedAsync(context, session, correlationId, request.Operation, message, request.RawRequestJson);
            PreAuthSimulationResponse stateConflictResponse = JsonResponse(StatusCodes.Status409Conflict, new { message, status = ToStatusName(session.Status) });
            await LogResponseAsync(context, session, correlationId, request.Operation, stateConflictResponse.StatusCode, stateConflictResponse.Body, request.RawRequestJson);
            await dbContext.SaveChangesAsync(cancellationToken);
            return stateConflictResponse;
        }

        List<PreAuthTimelineEntry> timeline = DeserializeTimeline(session.TimelineJson);
        AppendTimeline(
            timeline,
            DateTimeOffset.UtcNow,
            request.Operation,
            "RequestReceived",
            "Captured FCC pre-auth authorize request.",
            metadata: new
            {
                request.Method,
                request.Path,
            });

        session.RawRequestJson = NormalizeJson(request.RawRequestJson);
        session.CanonicalRequestJson = canonicalRequest;

        if (ShouldFail(context.Profile.Contract, payload, request.Operation, correlationId))
        {
            PreAuthSimulationResponse failureResponse = await FailSessionAsync(
                context,
                session,
                timeline,
                request,
                payload,
                cancellationToken);
            return failureResponse;
        }

        if (context.Profile.Contract.FailureSimulation.SimulatedDelayMs > 0)
        {
            await Task.Delay(context.Profile.Contract.FailureSimulation.SimulatedDelayMs, cancellationToken);
        }

        session.AuthorizedAmount = payload.Amount ?? session.ReservedAmount;
        AppendTransition(context, session, timeline, PreAuthSessionStatus.Pending, PreAuthSessionStatus.Authorized, DateTimeOffset.UtcNow, request.Operation, "Authorized the pre-auth session.");

        string responseStatus = ToResponseStatus(session.Status);
        Dictionary<string, string> sampleValues = BuildResponseValues(context, session, request.Operation, responseStatus);
        string responseBody = RenderResponseBody(context.Profile.Contract, request.Operation, sampleValues);
        session.RawResponseJson = responseBody;
        session.CanonicalResponseJson = Serialize(new
        {
            status = ToStatusName(session.Status),
            preAuthId = session.ExternalReference,
            correlationId = session.CorrelationId,
            expiresAtUtc = session.ExpiresAtUtc,
        });
        AppendTimeline(
            timeline,
            DateTimeOffset.UtcNow,
            request.Operation,
            "ResponseSent",
            "Returned authorize response.",
            metadata: new
            {
                status = responseStatus,
            });
        session.TimelineJson = Serialize(timeline);

        PreAuthSimulationResponse response = new(StatusCodes.Status200OK, "application/json", responseBody);
        await LogResponseAsync(context, session, correlationId, request.Operation, response.StatusCode, response.Body, request.RawRequestJson);
        await dbContext.SaveChangesAsync(cancellationToken);
        return response;
    }

    private async Task<PreAuthSimulationResponse> HandleCancelAsync(
        SiteProfileContext context,
        PreAuthSimulationRequest request,
        ParsedPreAuthRequest payload,
        CancellationToken cancellationToken)
    {
        string correlationId = ResolveCorrelationId(payload, request);
        string canonicalRequest = Serialize(new
        {
            siteCode = context.Site.SiteCode,
            preAuthId = payload.PreAuthId,
            correlationId,
        });

        await LogRequestAsync(context, null, correlationId, request, canonicalRequest);

        if (string.IsNullOrWhiteSpace(payload.PreAuthId))
        {
            List<string> errors = ["preauthId is required."];
            await LogValidationFailureAsync(context, null, correlationId, request.Operation, errors, request.RawRequestJson);
            PreAuthSimulationResponse invalidResponse = JsonResponse(
                StatusCodes.Status400BadRequest,
                new
                {
                    message = "Pre-auth cancel validation failed.",
                    errors,
                });
            await LogResponseAsync(context, null, correlationId, request.Operation, invalidResponse.StatusCode, invalidResponse.Body, request.RawRequestJson);
            await dbContext.SaveChangesAsync(cancellationToken);
            return invalidResponse;
        }

        PreAuthSession? session = await dbContext.PreAuthSessions
            .SingleOrDefaultAsync(
                x => x.SiteId == context.Site.Id && x.ExternalReference == payload.PreAuthId,
                cancellationToken);

        if (session is null)
        {
            string message = $"Pre-auth id '{payload.PreAuthId}' was not found.";
            await LogSequenceRejectedAsync(context, null, correlationId, request.Operation, message, request.RawRequestJson);
            PreAuthSimulationResponse notFoundResponse = JsonResponse(StatusCodes.Status404NotFound, new { message });
            await LogResponseAsync(context, null, correlationId, request.Operation, notFoundResponse.StatusCode, notFoundResponse.Body, request.RawRequestJson);
            await dbContext.SaveChangesAsync(cancellationToken);
            return notFoundResponse;
        }

        if (await TryExpireSessionAsync(context, session, cancellationToken))
        {
            string message = $"Pre-auth id '{payload.PreAuthId}' already expired.";
            await LogSequenceRejectedAsync(context, session, correlationId, request.Operation, message, request.RawRequestJson);
            PreAuthSimulationResponse expiredResponse = JsonResponse(StatusCodes.Status409Conflict, new { message, status = ToStatusName(session.Status) });
            await LogResponseAsync(context, session, correlationId, request.Operation, expiredResponse.StatusCode, expiredResponse.Body, request.RawRequestJson);
            await dbContext.SaveChangesAsync(cancellationToken);
            return expiredResponse;
        }

        if (session.Status is not (PreAuthSessionStatus.Pending or PreAuthSessionStatus.Authorized))
        {
            string message = $"Cannot cancel session '{session.ExternalReference}' from state '{ToStatusName(session.Status)}'.";
            await LogSequenceRejectedAsync(context, session, correlationId, request.Operation, message, request.RawRequestJson);
            PreAuthSimulationResponse conflictResponse = JsonResponse(StatusCodes.Status409Conflict, new { message, status = ToStatusName(session.Status) });
            await LogResponseAsync(context, session, correlationId, request.Operation, conflictResponse.StatusCode, conflictResponse.Body, request.RawRequestJson);
            await dbContext.SaveChangesAsync(cancellationToken);
            return conflictResponse;
        }

        List<PreAuthTimelineEntry> timeline = DeserializeTimeline(session.TimelineJson);
        AppendTimeline(
            timeline,
            DateTimeOffset.UtcNow,
            request.Operation,
            "RequestReceived",
            "Captured FCC pre-auth cancel request.",
            metadata: new
            {
                request.Method,
                request.Path,
            });

        session.RawRequestJson = NormalizeJson(request.RawRequestJson);
        session.CanonicalRequestJson = canonicalRequest;

        if (ShouldFail(context.Profile.Contract, payload, request.Operation, correlationId))
        {
            PreAuthSimulationResponse failureResponse = await FailSessionAsync(
                context,
                session,
                timeline,
                request,
                payload,
                cancellationToken);
            return failureResponse;
        }

        if (context.Profile.Contract.FailureSimulation.SimulatedDelayMs > 0)
        {
            await Task.Delay(context.Profile.Contract.FailureSimulation.SimulatedDelayMs, cancellationToken);
        }

        AppendTransition(context, session, timeline, session.Status, PreAuthSessionStatus.Cancelled, DateTimeOffset.UtcNow, request.Operation, "Cancelled the pre-auth session.");

        string responseStatus = ToResponseStatus(session.Status);
        Dictionary<string, string> sampleValues = BuildResponseValues(context, session, request.Operation, responseStatus);
        string responseBody = RenderResponseBody(context.Profile.Contract, request.Operation, sampleValues);
        session.RawResponseJson = responseBody;
        session.CanonicalResponseJson = Serialize(new
        {
            status = ToStatusName(session.Status),
            preAuthId = session.ExternalReference,
            correlationId = session.CorrelationId,
        });
        AppendTimeline(
            timeline,
            DateTimeOffset.UtcNow,
            request.Operation,
            "ResponseSent",
            "Returned cancel response.",
            metadata: new
            {
                status = responseStatus,
            });
        session.TimelineJson = Serialize(timeline);

        PreAuthSimulationResponse response = new(StatusCodes.Status200OK, "application/json", responseBody);
        await LogResponseAsync(context, session, correlationId, request.Operation, response.StatusCode, response.Body, request.RawRequestJson);
        await dbContext.SaveChangesAsync(cancellationToken);
        return response;
    }

    private async Task<PreAuthSimulationResponse> FailSessionAsync(
        SiteProfileContext context,
        PreAuthSession session,
        List<PreAuthTimelineEntry> timeline,
        PreAuthSimulationRequest request,
        ParsedPreAuthRequest payload,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string correlationId = ResolveCorrelationId(payload, request);
        FccFailureSimulationDefinition failureSimulation = context.Profile.Contract.FailureSimulation;
        int statusCode = payload.FailureStatusCode ?? failureSimulation.HttpStatusCode;
        string errorCode = string.IsNullOrWhiteSpace(payload.FailureCode)
            ? string.IsNullOrWhiteSpace(failureSimulation.ErrorCode) ? "SIMULATED_FAILURE" : failureSimulation.ErrorCode
            : payload.FailureCode;

        string message = !string.IsNullOrWhiteSpace(payload.FailureMessage)
            ? payload.FailureMessage
            : string.IsNullOrWhiteSpace(failureSimulation.MessageTemplate)
                ? $"Simulated failure for {request.Operation}."
                : FccProfileTemplateRenderer.Render(
                    failureSimulation.MessageTemplate,
                    BuildResponseValues(context, session, request.Operation, "failed"));

        PreAuthSessionStatus previousStatus = session.Status;
        AppendTransition(context, session, timeline, previousStatus, PreAuthSessionStatus.Failed, now, request.Operation, message, new { errorCode });
        AppendTimeline(
            timeline,
            now,
            request.Operation,
            "FailureInjected",
            "Applied simulated failure hook.",
            metadata: new
            {
                errorCode,
                statusCode,
            });

        string responseBody = Serialize(new
        {
            status = "failed",
            errorCode,
            message,
            preauthId = session.ExternalReference,
            correlationId = session.CorrelationId,
        });

        session.RawResponseJson = responseBody;
        session.CanonicalResponseJson = responseBody;
        session.TimelineJson = Serialize(timeline);

        dbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            SiteId = context.Site.Id,
            FccSimulatorProfileId = context.ProfileId,
            PreAuthSessionId = session.Id,
            CorrelationId = correlationId,
            Severity = "Warning",
            Category = "PreAuthSequence",
            EventType = "PreAuthFailureInjected",
            Message = message,
            RawPayloadJson = NormalizeJson(request.RawRequestJson),
            CanonicalPayloadJson = responseBody,
            MetadataJson = Serialize(new
            {
                request.Operation,
                errorCode,
                statusCode,
            }),
            OccurredAtUtc = now,
        });

        PreAuthSimulationResponse response = new(statusCode, "application/json", responseBody);
        await LogResponseAsync(context, session, correlationId, request.Operation, response.StatusCode, response.Body, request.RawRequestJson);
        await dbContext.SaveChangesAsync(cancellationToken);
        return response;
    }

    private async Task<SiteProfileContext?> LoadContextAsync(string siteCode, CancellationToken cancellationToken)
    {
        Site? site = await dbContext.Sites
            .Include(x => x.ActiveFccSimulatorProfile)
            .SingleOrDefaultAsync(x => x.SiteCode == siteCode && x.IsActive, cancellationToken);

        if (site is null || !site.ActiveFccSimulatorProfile.IsActive)
        {
            return null;
        }

        FccProfileRecord record = FccProfileService.ToRecord(site.ActiveFccSimulatorProfile);

        return new SiteProfileContext(site, record);
    }

    private async Task ExpireSiteSessionsAsync(SiteProfileContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<PreAuthSession> sessions = await dbContext.PreAuthSessions
            .Where(x =>
                x.SiteId == context.Site.Id &&
                x.ExpiresAtUtc.HasValue &&
                x.ExpiresAtUtc <= now &&
                (x.Status == PreAuthSessionStatus.Pending ||
                 x.Status == PreAuthSessionStatus.Authorized ||
                 x.Status == PreAuthSessionStatus.Dispensing))
            .ToListAsync(cancellationToken);

        if (sessions.Count == 0)
        {
            return;
        }

        foreach (PreAuthSession session in sessions)
        {
            ExpireSession(session, context.ProfileId, now, "Session expired before the next FCC pre-auth action.");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> TryExpireSessionAsync(SiteProfileContext context, PreAuthSession session, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!session.ExpiresAtUtc.HasValue || session.ExpiresAtUtc > now || !IsActiveStatus(session.Status))
        {
            return false;
        }

        ExpireSession(session, context.ProfileId, now, "Session expired before the request could be processed.");
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void ExpireSession(PreAuthSession session, Guid profileId, DateTimeOffset now, string message)
    {
        List<PreAuthTimelineEntry> timeline = DeserializeTimeline(session.TimelineJson);
        PreAuthSessionStatus previousStatus = session.Status;
        session.Status = PreAuthSessionStatus.Expired;
        session.CompletedAtUtc = now;
        session.TimelineJson = SerializeTimelineWithTransition(timeline, previousStatus, session.Status, now, "expiry", message);

        dbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            SiteId = session.SiteId,
            FccSimulatorProfileId = profileId,
            PreAuthSessionId = session.Id,
            CorrelationId = session.CorrelationId,
            Severity = "Warning",
            Category = "StateTransition",
            EventType = "PreAuthExpired",
            Message = message,
            RawPayloadJson = session.RawRequestJson,
            CanonicalPayloadJson = session.RawResponseJson,
            MetadataJson = Serialize(new
            {
                fromStatus = ToStatusName(previousStatus),
                toStatus = ToStatusName(session.Status),
                session.ExpiresAtUtc,
            }),
            OccurredAtUtc = now,
        });
    }

    private async Task<PumpNozzleMatch?> LoadPumpNozzleMatchAsync(Guid siteId, int pumpNumber, int nozzleNumber, CancellationToken cancellationToken)
    {
        return await dbContext.Nozzles
            .Where(x => x.Pump.SiteId == siteId && x.Pump.PumpNumber == pumpNumber && x.NozzleNumber == nozzleNumber)
            .Select(x => new PumpNozzleMatch(x.PumpId, x.Id))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task LogRequestAsync(
        SiteProfileContext context,
        PreAuthSession? session,
        string correlationId,
        PreAuthSimulationRequest request,
        string canonicalPayload)
    {
        dbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            SiteId = context.Site.Id,
            FccSimulatorProfileId = context.ProfileId,
            PreAuthSessionId = session?.Id,
            CorrelationId = correlationId,
            Severity = "Information",
            Category = "FccRequest",
            EventType = ToEventType(request.Operation, "Requested"),
            Message = $"Received FCC {request.Operation} request for site '{context.Site.SiteCode}'.",
            RawPayloadJson = NormalizeJson(request.RawRequestJson),
            CanonicalPayloadJson = canonicalPayload,
            MetadataJson = Serialize(new
            {
                request.Method,
                request.Path,
                request.TraceIdentifier,
            }),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });

        await Task.CompletedTask;
    }

    private async Task LogResponseAsync(
        SiteProfileContext context,
        PreAuthSession? session,
        string correlationId,
        string operation,
        int statusCode,
        string responseBody,
        string requestBody)
    {
        dbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            SiteId = context.Site.Id,
            FccSimulatorProfileId = context.ProfileId,
            PreAuthSessionId = session?.Id,
            CorrelationId = correlationId,
            Severity = statusCode >= 400 ? "Warning" : "Information",
            Category = "FccResponse",
            EventType = ToEventType(operation, "Responded"),
            Message = $"Returned FCC {operation} response with status code {statusCode}.",
            RawPayloadJson = responseBody,
            CanonicalPayloadJson = responseBody,
            MetadataJson = Serialize(new
            {
                statusCode,
                requestPayload = NormalizeJson(requestBody),
            }),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });

        await Task.CompletedTask;
    }

    private async Task LogValidationFailureAsync(
        SiteProfileContext context,
        PreAuthSession? session,
        string correlationId,
        string operation,
        IReadOnlyList<string> errors,
        string requestBody)
    {
        dbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            SiteId = context.Site.Id,
            FccSimulatorProfileId = context.ProfileId,
            PreAuthSessionId = session?.Id,
            CorrelationId = correlationId,
            Severity = "Warning",
            Category = "PreAuthSequence",
            EventType = "PreAuthValidationFailed",
            Message = $"Validation failed for {operation}.",
            RawPayloadJson = NormalizeJson(requestBody),
            CanonicalPayloadJson = "{}",
            MetadataJson = Serialize(new
            {
                operation,
                errors,
            }),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });

        await Task.CompletedTask;
    }

    private async Task LogSequenceRejectedAsync(
        SiteProfileContext context,
        PreAuthSession? session,
        string correlationId,
        string operation,
        string message,
        string requestBody)
    {
        dbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            SiteId = context.Site.Id,
            FccSimulatorProfileId = context.ProfileId,
            PreAuthSessionId = session?.Id,
            CorrelationId = correlationId,
            Severity = "Warning",
            Category = "PreAuthSequence",
            EventType = "PreAuthSequenceRejected",
            Message = message,
            RawPayloadJson = NormalizeJson(requestBody),
            CanonicalPayloadJson = "{}",
            MetadataJson = Serialize(new
            {
                operation,
                currentStatus = session is null ? null : ToStatusName(session.Status),
            }),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });

        await Task.CompletedTask;
    }

    private void AppendTransition(
        SiteProfileContext context,
        PreAuthSession session,
        List<PreAuthTimelineEntry> timeline,
        PreAuthSessionStatus? fromStatus,
        PreAuthSessionStatus toStatus,
        DateTimeOffset occurredAtUtc,
        string operation,
        string message,
        object? metadata = null)
    {
        session.Status = toStatus;
        if (toStatus == PreAuthSessionStatus.Authorized)
        {
            session.AuthorizedAtUtc ??= occurredAtUtc;
        }

        if (toStatus is PreAuthSessionStatus.Completed or PreAuthSessionStatus.Cancelled or PreAuthSessionStatus.Expired or PreAuthSessionStatus.Failed)
        {
            session.CompletedAtUtc = occurredAtUtc;
        }

        AppendTimeline(timeline, occurredAtUtc, operation, "StateTransition", message, fromStatus, toStatus, metadata);
        session.TimelineJson = Serialize(timeline);

        dbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            SiteId = context.Site.Id,
            FccSimulatorProfileId = context.ProfileId,
            PreAuthSessionId = session.Id,
            CorrelationId = session.CorrelationId,
            Severity = "Information",
            Category = "StateTransition",
            EventType = "PreAuthStatusChanged",
            Message = message,
            RawPayloadJson = session.RawRequestJson,
            CanonicalPayloadJson = session.RawResponseJson,
            MetadataJson = Serialize(new
            {
                operation,
                fromStatus = fromStatus is null ? null : ToStatusName(fromStatus.Value),
                toStatus = ToStatusName(toStatus),
                metadata,
            }),
            OccurredAtUtc = occurredAtUtc,
        });
    }

    private static void AppendTimeline(
        List<PreAuthTimelineEntry> timeline,
        DateTimeOffset occurredAtUtc,
        string operation,
        string eventType,
        string message,
        PreAuthSessionStatus? fromStatus = null,
        PreAuthSessionStatus? toStatus = null,
        object? metadata = null)
    {
        timeline.Add(new PreAuthTimelineEntry(
            occurredAtUtc,
            operation,
            eventType,
            message,
            fromStatus is null ? null : ToStatusName(fromStatus.Value),
            toStatus is null ? null : ToStatusName(toStatus.Value),
            metadata));
    }

    private static Dictionary<string, string> BuildResponseValues(
        SiteProfileContext context,
        PreAuthSession session,
        string operation,
        string responseStatus)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["siteCode"] = context.Site.SiteCode,
            ["profileKey"] = context.Profile.ProfileKey,
            ["operation"] = operation,
            ["preauthId"] = session.ExternalReference,
            ["correlationId"] = session.CorrelationId,
            ["status"] = responseStatus,
            ["amount"] = session.ReservedAmount.ToString("0.##", CultureInfo.InvariantCulture),
            ["expiresAtUtc"] = session.ExpiresAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    private static string RenderResponseBody(FccProfileContract contract, string operation, IReadOnlyDictionary<string, string> sampleValues)
    {
        FccTemplateDefinition template = contract.ResponseTemplates
            .Single(x => string.Equals(x.Operation, operation, StringComparison.OrdinalIgnoreCase));

        return FccProfileTemplateRenderer.Render(template.BodyTemplate, sampleValues);
    }

    private static bool IsOperationEnabled(FccProfileContract contract, string operation)
    {
        return contract.EndpointSurface.Any(x => x.Enabled && string.Equals(x.Operation, operation, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldFail(FccProfileContract contract, ParsedPreAuthRequest payload, string operation, string correlationId)
    {
        if (payload.SimulateFailure)
        {
            return true;
        }

        FccFailureSimulationDefinition failureSimulation = contract.FailureSimulation;
        if (!failureSimulation.Enabled || failureSimulation.FailureRatePercent <= 0)
        {
            return false;
        }

        if (failureSimulation.FailureRatePercent >= 100)
        {
            return true;
        }

        int bucket = Math.Abs(HashCode.Combine(operation, correlationId)) % 100;
        return bucket < failureSimulation.FailureRatePercent;
    }

    private static string ResolveCorrelationId(ParsedPreAuthRequest payload, PreAuthSimulationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(payload.CorrelationId))
        {
            return payload.CorrelationId;
        }

        if (!string.IsNullOrWhiteSpace(payload.PreAuthId))
        {
            return payload.PreAuthId;
        }

        return request.TraceIdentifier;
    }

    private static ParsedPreAuthRequest ParseRequest(PreAuthSimulationRequest request)
    {
        IReadOnlyDictionary<string, string> fields = request.Fields;

        return new ParsedPreAuthRequest(
            GetString(fields, "preauthId", "preAuthId", "externalReference"),
            GetString(fields, "correlationId"),
            GetInt(fields, "pump", "pumpNumber"),
            GetInt(fields, "nozzle", "nozzleNumber"),
            GetDecimal(fields, "amount", "authorizedAmount"),
            GetInt(fields, "expiresInSeconds", "expirySeconds", "ttlSeconds"),
            GetBool(fields, "simulateFailure"),
            GetInt(fields, "failureStatusCode", "statusCode"),
            GetString(fields, "failureMessage"),
            GetString(fields, "failureCode"));
    }

    private static int ResolveExpirySeconds(Site site, FccProfileContract contract, ParsedPreAuthRequest payload)
    {
        if (payload.ExpiresInSeconds.HasValue)
        {
            return Math.Max(0, payload.ExpiresInSeconds.Value);
        }

        if (TryGetJsonInt(site.SettingsJson, "preAuthExpirySeconds", out int siteValue))
        {
            return Math.Max(0, siteValue);
        }

        if (contract.Extensions.Configuration.TryGetValue("preAuthExpirySeconds", out string? profileValue) &&
            int.TryParse(profileValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedProfileValue))
        {
            return Math.Max(0, parsedProfileValue);
        }

        return 300;
    }

    private static bool TryGetJsonInt(string json, string propertyName, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(propertyName, out JsonElement property) ||
                property.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            value = property.GetInt32();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<PreAuthTimelineEntry> DeserializeTimeline(string timelineJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<PreAuthTimelineEntry>>(timelineJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string SerializeTimelineWithTransition(
        List<PreAuthTimelineEntry> timeline,
        PreAuthSessionStatus fromStatus,
        PreAuthSessionStatus toStatus,
        DateTimeOffset occurredAtUtc,
        string operation,
        string message)
    {
        AppendTimeline(timeline, occurredAtUtc, operation, "StateTransition", message, fromStatus, toStatus);
        return Serialize(timeline);
    }

    private static bool IsActiveStatus(PreAuthSessionStatus status)
    {
        return status is PreAuthSessionStatus.Pending or PreAuthSessionStatus.Authorized or PreAuthSessionStatus.Dispensing;
    }

    private static string NormalizeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        return json;
    }

    private static string ToEventType(string operation, string suffix)
    {
        return operation switch
        {
            "preauth-create" => $"PreAuthCreate{suffix}",
            "preauth-authorize" => $"PreAuthAuthorize{suffix}",
            "preauth-cancel" => $"PreAuthCancel{suffix}",
            _ => $"PreAuth{suffix}",
        };
    }

    private static string ToStatusName(PreAuthSessionStatus status)
    {
        return status switch
        {
            PreAuthSessionStatus.Pending => "PENDING",
            PreAuthSessionStatus.Authorized => "AUTHORIZED",
            PreAuthSessionStatus.Dispensing => "DISPENSING",
            PreAuthSessionStatus.Completed => "COMPLETED",
            PreAuthSessionStatus.Cancelled => "CANCELLED",
            PreAuthSessionStatus.Expired => "EXPIRED",
            PreAuthSessionStatus.Failed => "FAILED",
            _ => status.ToString().ToUpperInvariant(),
        };
    }

    private static string ToModeName(PreAuthFlowMode mode)
    {
        return mode switch
        {
            PreAuthFlowMode.CreateOnly => "CREATE_ONLY",
            PreAuthFlowMode.CreateThenAuthorize => "CREATE_THEN_AUTHORIZE",
            _ => mode.ToString().ToUpperInvariant(),
        };
    }

    private static string ToResponseStatus(PreAuthSessionStatus status)
    {
        return ToStatusName(status).ToLowerInvariant();
    }

    private static PreAuthSimulationResponse JsonResponse(int statusCode, object payload)
    {
        return new PreAuthSimulationResponse(statusCode, "application/json", Serialize(payload));
    }

    private static string Serialize(object value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static string? GetString(IReadOnlyDictionary<string, string> fields, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (fields.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static int? GetInt(IReadOnlyDictionary<string, string> fields, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (fields.TryGetValue(key, out string? value) &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static decimal? GetDecimal(IReadOnlyDictionary<string, string> fields, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (fields.TryGetValue(key, out string? value) &&
                decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> fields, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (fields.TryGetValue(key, out string? value) &&
                bool.TryParse(value, out bool parsed))
            {
                return parsed;
            }
        }

        return false;
    }

    private sealed record SiteProfileContext(Site Site, FccProfileRecord Profile)
    {
        public Guid ProfileId => Site.ActiveFccSimulatorProfileId;
    }

    private sealed record PumpNozzleMatch(Guid PumpId, Guid NozzleId);

    private sealed record ParsedPreAuthRequest(
        string? PreAuthId,
        string? CorrelationId,
        int? PumpNumber,
        int? NozzleNumber,
        decimal? Amount,
        int? ExpiresInSeconds,
        bool SimulateFailure,
        int? FailureStatusCode,
        string? FailureMessage,
        string? FailureCode);

    private sealed record PreAuthTimelineEntry(
        DateTimeOffset OccurredAtUtc,
        string Operation,
        string EventType,
        string Message,
        string? FromStatus,
        string? ToStatus,
        object? Metadata);
}
