using System.Text.Json;
using FccMiddleware.Application.Common;
using FccMiddleware.Application.DeadLetter;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Exceptions;
using FccMiddleware.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.PreAuth;

/// <summary>
/// Handles ForwardPreAuthCommand.
/// Pipeline: dedup lookup → create or update → state transition → outbox event → persist.
/// Dedup key: (odooOrderId, siteCode). Terminal-status records allow re-request (new record).
/// </summary>
public sealed class ForwardPreAuthHandler
    : IRequestHandler<ForwardPreAuthCommand, Result<ForwardPreAuthResult>>
{
    private static readonly PreAuthStatus[] TerminalStatuses =
    [
        PreAuthStatus.COMPLETED, PreAuthStatus.CANCELLED,
        PreAuthStatus.EXPIRED, PreAuthStatus.FAILED
    ];

    private readonly IPreAuthDbContext _db;
    private readonly IEventPublisher _eventPublisher;
    private readonly IDeadLetterService _deadLetterService;
    private readonly ILogger<ForwardPreAuthHandler> _logger;

    public ForwardPreAuthHandler(
        IPreAuthDbContext db,
        IEventPublisher eventPublisher,
        IDeadLetterService deadLetterService,
        ILogger<ForwardPreAuthHandler> logger)
    {
        _db = db;
        _eventPublisher = eventPublisher;
        _deadLetterService = deadLetterService;
        _logger = logger;
    }

    public async Task<Result<ForwardPreAuthResult>> Handle(
        ForwardPreAuthCommand command,
        CancellationToken cancellationToken)
    {
        // ── Optimistic path: attempt insert first (PA-P06 optimization). ───────
        // The common case is a new pre-auth, which completes in a single DB
        // round-trip. The filtered unique index ix_preauth_idemp causes a
        // constraint violation when a non-terminal duplicate exists, triggering
        // the fallback path below.
        return await CreateNewAsync(command, cancellationToken);
    }

    private async Task<Result<ForwardPreAuthResult>> UpdateExistingAsync(
        PreAuthRecord existing,
        ForwardPreAuthCommand command,
        CancellationToken cancellationToken)
    {
        if (existing.Status == command.Status)
        {
            // Idempotent: same status — apply any mutable field updates from the retry
            // (e.g. FccCorrelationId, CustomerName arriving on a retried forward after network failure)
            ApplyMutableFields(existing, command);
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Idempotent pre-auth forward for {OdooOrderId}/{SiteCode}, status already {Status}",
                command.OdooOrderId, command.SiteCode, command.Status);

            return Result<ForwardPreAuthResult>.Success(new ForwardPreAuthResult
            {
                PreAuthId = existing.Id,
                Status = existing.Status,
                Created = false,
                SiteCode = existing.SiteCode,
                OdooOrderId = existing.OdooOrderId,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = existing.UpdatedAt
            });
        }

        try
        {
            existing.Transition(command.Status);
        }
        catch (InvalidPreAuthTransitionException ex)
        {
            _logger.LogWarning(
                "Invalid pre-auth transition for {OdooOrderId}/{SiteCode}: {From} → {To}",
                command.OdooOrderId, command.SiteCode, ex.From, ex.To);

            var errorMsg = $"Cannot transition pre-auth from {ex.From} to {ex.To}.";
            await _deadLetterService.CreateAsync(
                command.LegalEntityId, command.SiteCode,
                DeadLetterType.PRE_AUTH, DeadLetterReason.VALIDATION_FAILURE,
                "CONFLICT.INVALID_TRANSITION", errorMsg,
                rawPayloadJson: JsonSerializer.Serialize(new { command.OdooOrderId, command.SiteCode, command.Status }),
                cancellationToken: cancellationToken);

            return Result<ForwardPreAuthResult>.Failure(
                "CONFLICT.INVALID_TRANSITION", errorMsg);
        }

        // Apply timestamp for the new status
        ApplyStatusTimestamp(existing, command.Status);

        ApplyMutableFields(existing, command);
        _eventPublisher.Publish(PreAuthEventFactory.CreateForStatus(
            existing,
            command.CorrelationId,
            source: "cloud-preauth"));

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (IsUniqueConstraintViolation(ex))
        {
            _db.ClearTracked();
            _logger.LogWarning(
                "Unique constraint race on pre-auth {OdooOrderId}/{SiteCode}; retrying lookup.",
                command.OdooOrderId, command.SiteCode);

            return Result<ForwardPreAuthResult>.Failure(
                "CONFLICT.RACE_CONDITION",
                "Concurrent update detected. Please retry.");
        }

        _logger.LogInformation(
            "Pre-auth {PreAuthId} updated to {Status} for {OdooOrderId}/{SiteCode}",
            existing.Id, existing.Status, command.OdooOrderId, command.SiteCode);

        return Result<ForwardPreAuthResult>.Success(new ForwardPreAuthResult
        {
            PreAuthId = existing.Id,
            Status = existing.Status,
            Created = false,
            SiteCode = existing.SiteCode,
            OdooOrderId = existing.OdooOrderId,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = existing.UpdatedAt
        });
    }

    private async Task<Result<ForwardPreAuthResult>> CreateNewAsync(
        ForwardPreAuthCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new PreAuthRecord
        {
            Id = Guid.NewGuid(),
            LegalEntityId = command.LegalEntityId,
            SiteCode = command.SiteCode,
            OdooOrderId = command.OdooOrderId,
            PumpNumber = command.PumpNumber,
            NozzleNumber = command.NozzleNumber,
            ProductCode = command.ProductCode,
            RequestedAmountMinorUnits = command.RequestedAmountMinorUnits,
            UnitPriceMinorPerLitre = command.UnitPriceMinorPerLitre,
            CurrencyCode = command.CurrencyCode,
            Status = command.Status,
            RequestedAt = command.RequestedAt,
            ExpiresAt = command.ExpiresAt,
            SchemaVersion = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        ApplyMutableFields(record, command);

        // Apply timestamp for the initial status (if not PENDING, Edge already progressed)
        ApplyStatusTimestamp(record, command.Status);

        _db.AddPreAuthRecord(record);
        _eventPublisher.Publish(PreAuthEventFactory.CreateForStatus(
            record,
            command.CorrelationId,
            source: "cloud-preauth"));

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (IsUniqueConstraintViolation(ex))
        {
            // Constraint violation: an existing non-terminal record exists.
            // Fall back to lookup + update (PA-P06: this is the rare path).
            _db.ClearTracked();

            _logger.LogInformation(
                "Unique constraint on pre-auth {OdooOrderId}/{SiteCode}; falling back to lookup + update.",
                command.OdooOrderId, command.SiteCode);

            var raceExisting = await _db.FindByDedupKeyAsync(
                command.OdooOrderId, command.SiteCode, cancellationToken);

            if (raceExisting is not null && !IsTerminal(raceExisting.Status))
            {
                // Existing non-terminal record — attempt status transition
                return await UpdateExistingAsync(raceExisting, command, cancellationToken);
            }

            if (raceExisting is not null)
            {
                return Result<ForwardPreAuthResult>.Success(new ForwardPreAuthResult
                {
                    PreAuthId = raceExisting.Id,
                    Status = raceExisting.Status,
                    Created = false,
                    SiteCode = raceExisting.SiteCode,
                    OdooOrderId = raceExisting.OdooOrderId,
                    CreatedAt = raceExisting.CreatedAt,
                    UpdatedAt = raceExisting.UpdatedAt
                });
            }

            return Result<ForwardPreAuthResult>.Failure(
                "CONFLICT.RACE_CONDITION",
                "Concurrent insert detected and recovery failed. Please retry.");
        }

        _logger.LogInformation(
            "Pre-auth {PreAuthId} created with status {Status} for {OdooOrderId}/{SiteCode}",
            record.Id, record.Status, command.OdooOrderId, command.SiteCode);

        return Result<ForwardPreAuthResult>.Success(new ForwardPreAuthResult
        {
            PreAuthId = record.Id,
            Status = record.Status,
            Created = true,
            SiteCode = record.SiteCode,
            OdooOrderId = record.OdooOrderId,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt
        });
    }

    private static void ApplyStatusTimestamp(PreAuthRecord record, PreAuthStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        switch (status)
        {
            case PreAuthStatus.AUTHORIZED:
                record.AuthorizedAt ??= now;
                break;
            case PreAuthStatus.DISPENSING:
                record.DispensingAt ??= now;
                break;
            case PreAuthStatus.COMPLETED:
                record.CompletedAt ??= now;
                break;
            case PreAuthStatus.CANCELLED:
                record.CancelledAt ??= now;
                break;
            case PreAuthStatus.EXPIRED:
                record.ExpiredAt ??= now;
                break;
            case PreAuthStatus.FAILED:
                record.FailedAt ??= now;
                break;
        }
    }

    private static void ApplyMutableFields(PreAuthRecord record, ForwardPreAuthCommand command)
    {
        if (command.FccCorrelationId is not null)
            record.FccCorrelationId = command.FccCorrelationId;

        if (command.FccAuthorizationCode is not null)
        {
            record.FccAuthorizationCode = command.FccAuthorizationCode;
            record.AuthorizedAmountMinorUnits ??= record.RequestedAmountMinorUnits;
        }

        if (command.VehicleNumber is not null)
            record.VehicleNumber = command.VehicleNumber;

        if (command.CustomerName is not null)
            record.CustomerName = command.CustomerName;

        if (command.CustomerTaxId is not null)
            record.CustomerTaxId = command.CustomerTaxId;

        if (command.CustomerBusinessName is not null)
            record.CustomerBusinessName = command.CustomerBusinessName;

        if (command.AttendantId is not null)
            record.AttendantId = command.AttendantId;
    }

    private static bool IsTerminal(PreAuthStatus status) =>
        Array.IndexOf(TerminalStatuses, status) >= 0;

    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("23505")
            || message.Contains("ix_preauth_idemp")
            || message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);
    }
}
