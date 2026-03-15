using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PreAuthEntity = FccDesktopAgent.Core.Buffer.Entities.PreAuthRecord;

namespace FccDesktopAgent.Core.PreAuth;

/// <summary>
/// Handles pre-authorization commands from Odoo POS over the station LAN.
///
/// Architecture rules applied:
///   #11: POST /api/preauth response is based on LAN-only work. Cloud forwarding is always async.
///   #3:  Local record is persisted before FCC call so no pre-auth is lost.
///   #14: Pre-auth always travels over LAN; internet state is irrelevant to FCC call path.
/// </summary>
public sealed class PreAuthHandler : IPreAuthHandler
{
    private readonly AgentDbContext _db;
    private readonly IFccAdapterFactory? _adapterFactory;
    private readonly IConnectivityMonitor _connectivity;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly IConfigManager? _configManager;
    private readonly ILogger<PreAuthHandler> _logger;

    public PreAuthHandler(
        AgentDbContext db,
        IConnectivityMonitor connectivity,
        IOptions<AgentConfiguration> config,
        ILogger<PreAuthHandler> logger,
        IFccAdapterFactory? adapterFactory = null,
        IConfigManager? configManager = null)
    {
        _db = db;
        _connectivity = connectivity;
        _config = config;
        _logger = logger;
        _adapterFactory = adapterFactory;
        _configManager = configManager;
    }

    // ── HandleAsync ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<PreAuthHandlerResult> HandleAsync(OdooPreAuthRequest request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var config = _config.Value;

        // ── Step 1: Local dedup check (AsNoTracking — common path is read-only) ──
        var existingRecords = await _db.PreAuths
            .AsNoTracking()
            .Where(p => p.OdooOrderId == request.OdooOrderId && p.SiteCode == request.SiteCode)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        var existingActive = existingRecords.FirstOrDefault(p => PreAuthStateMachine.IsActive(p.Status));

        if (existingActive is not null)
        {
            _logger.LogInformation(
                "Pre-auth dedup hit: returning existing record {Id} (status={Status}) for order {OrderId}",
                existingActive.Id, existingActive.Status, request.OdooOrderId);
            return PreAuthHandlerResult.Ok(existingActive);
        }

        // ── Step 2: Nozzle mapping lookup ─────────────────────────────────────
        var nozzle = await _db.NozzleMappings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                n => n.SiteCode == request.SiteCode
                     && n.OdooPumpNumber == request.OdooPumpNumber
                     && n.OdooNozzleNumber == request.OdooNozzleNumber,
                ct);

        if (nozzle is null)
        {
            _logger.LogWarning(
                "Pre-auth rejected: nozzle mapping not found for site={Site} pump={Pump} nozzle={Nozzle}",
                request.SiteCode, request.OdooPumpNumber, request.OdooNozzleNumber);
            return PreAuthHandlerResult.Fail(
                PreAuthHandlerError.NozzleMappingNotFound,
                $"No nozzle mapping for pump {request.OdooPumpNumber} nozzle {request.OdooNozzleNumber}");
        }

        if (!nozzle.IsActive)
        {
            _logger.LogWarning(
                "Pre-auth rejected: nozzle inactive site={Site} fccPump={FccPump} fccNozzle={FccNozzle}",
                request.SiteCode, nozzle.FccPumpNumber, nozzle.FccNozzleNumber);
            return PreAuthHandlerResult.Fail(
                PreAuthHandlerError.NozzleInactive,
                $"Nozzle pump={request.OdooPumpNumber} nozzle={request.OdooNozzleNumber} is not active");
        }

        // ── Step 3: Connectivity check — FCC must be reachable ────────────────
        var connectivity = _connectivity.Current;
        if (!connectivity.IsFccUp)
        {
            _logger.LogWarning(
                "Pre-auth rejected: FCC unreachable (state={State}) for order {OrderId}",
                connectivity.State, request.OdooOrderId);
            return PreAuthHandlerResult.Fail(
                PreAuthHandlerError.FccUnreachable,
                $"FCC is unreachable (connectivity state: {connectivity.State})");
        }

        if (_adapterFactory is null)
        {
            _logger.LogError("Pre-auth failed: FCC adapter factory is not registered");
            return PreAuthHandlerResult.Fail(
                PreAuthHandlerError.AdapterNotConfigured,
                "FCC adapter factory has not been configured for this agent");
        }

        // ── Step 4: Create new record with Pending status ────────────────────
        // Terminal history is preserved. The partial unique index only blocks coexistence
        // of multiple active records for the same (OdooOrderId, SiteCode).
        // Persist BEFORE the FCC call so the record is never lost even if the process crashes mid-call.
        var record = new PreAuthEntity { Id = Guid.NewGuid().ToString() };
        _db.PreAuths.Add(record);

        record.SiteCode = request.SiteCode;
        record.OdooOrderId = request.OdooOrderId;
        record.PumpNumber = nozzle.FccPumpNumber;
        record.NozzleNumber = nozzle.FccNozzleNumber;
        record.ProductCode = nozzle.ProductCode;
        record.RequestedAmount = request.RequestedAmountMinorUnits;
        record.UnitPrice = request.UnitPriceMinorPerLitre;
        record.Currency = request.Currency;
        record.Status = PreAuthStatus.Pending;
        record.IsCloudSynced = false;
        record.VehicleNumber = request.VehicleNumber;
        record.CustomerName = request.CustomerName;
        record.CustomerTaxId = request.CustomerTaxId;
        record.CustomerBusinessName = request.CustomerBusinessName;
        record.AttendantId = request.AttendantId;
        record.RequestedAt = now;
        record.ExpiresAt = now.AddMinutes(config.PreAuthExpiryMinutes);
        record.UpdatedAt = now;
        record.CreatedAt = now;

        var preAuthId = record.Id;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Pre-auth {Id} created (Pending) for order {OrderId} on FCC pump={Pump} nozzle={Nozzle}",
            preAuthId, request.OdooOrderId, nozzle.FccPumpNumber, nozzle.FccNozzleNumber);

        // ── Step 5: Call FCC adapter with translated pump/nozzle numbers ──────
        var fccCommand = new PreAuthCommand(
            PreAuthId: preAuthId,
            SiteCode: request.SiteCode,
            FccPumpNumber: nozzle.FccPumpNumber,
            FccNozzleNumber: nozzle.FccNozzleNumber,
            ProductCode: nozzle.ProductCode,
            RequestedAmountMinorUnits: request.RequestedAmountMinorUnits,
            UnitPriceMinorPerLitre: request.UnitPriceMinorPerLitre,
            Currency: request.Currency,
            VehicleNumber: request.VehicleNumber,
            FccCorrelationId: null,
            CustomerTaxId: request.CustomerTaxId,
            CustomerName: request.CustomerName);

        IFccAdapter adapter;
        try
        {
            var resolvedConfig = DesktopFccRuntimeConfiguration.Resolve(
                config,
                _configManager?.CurrentSiteConfig,
                TimeSpan.FromSeconds(config.PreAuthTimeoutSeconds),
                request.SiteCode);
            adapter = _adapterFactory.Create(resolvedConfig.Vendor, resolvedConfig.ConnectionConfig);
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex, "Pre-auth rejected: unsupported FCC vendor for site {Site}", request.SiteCode);
            return PreAuthHandlerResult.Fail(PreAuthHandlerError.UnsupportedVendor, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Pre-auth rejected: FCC configuration incomplete for site {Site}", request.SiteCode);
            return PreAuthHandlerResult.Fail(PreAuthHandlerError.AdapterNotConfigured, ex.Message);
        }

        PreAuthResult fccResult;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.PreAuthTimeoutSeconds));

        try
        {
            fccResult = await adapter.SendPreAuthAsync(fccCommand, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout fired — not an external cancellation
            _logger.LogWarning(
                "Pre-auth {Id} timed out after {Timeout}s waiting for FCC (order {OrderId})",
                preAuthId, config.PreAuthTimeoutSeconds, request.OdooOrderId);
            record.Status = PreAuthStatus.Failed;
            record.FailureReason = "FCC_TIMEOUT";
            record.FailedAt = DateTimeOffset.UtcNow;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            record.IsCloudSynced = false;
            await _db.SaveChangesAsync(CancellationToken.None);
            return PreAuthHandlerResult.Fail(PreAuthHandlerError.FccTimeout, "FCC call timed out");
        }

        // ── Step 6: Update record based on FCC response ───────────────────────
        record.UpdatedAt = DateTimeOffset.UtcNow;
        record.IsCloudSynced = false; // queued for cloud forwarding

        if (fccResult.Accepted)
        {
            record.Status = PreAuthStatus.Authorized;
            record.FccCorrelationId = fccResult.FccCorrelationId;
            record.FccAuthorizationCode = fccResult.FccAuthorizationCode;
            record.AuthorizedAt = DateTimeOffset.UtcNow;
            // Use FCC-provided expiry if available; fall back to configured duration
            record.ExpiresAt = fccResult.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(config.PreAuthExpiryMinutes);
        }
        else
        {
            record.Status = PreAuthStatus.Failed;
            record.FailureReason = fccResult.ErrorCode ?? "FCC_DECLINED";
            record.FailedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(CancellationToken.None);

        _logger.LogInformation(
            "Pre-auth {Id} for order {OrderId}: status={Status} authCode={Code}",
            preAuthId, request.OdooOrderId, record.Status, record.FccAuthorizationCode);

        if (fccResult.Accepted)
            return PreAuthHandlerResult.Ok(record);

        // PA-S04: Log the detailed FCC error message server-side; return only the
        // generic error code to the caller to avoid leaking FCC implementation details.
        if (!string.IsNullOrWhiteSpace(fccResult.ErrorMessage))
            _logger.LogWarning(
                "Pre-auth {Id} FCC declined detail: {Detail}",
                preAuthId, fccResult.ErrorMessage);

        return PreAuthHandlerResult.Fail(
            PreAuthHandlerError.FccDeclined,
            fccResult.ErrorCode ?? "FCC_DECLINED");
    }

    // ── CancelAsync ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<PreAuthHandlerResult> CancelAsync(
        string odooOrderId, string siteCode, CancellationToken ct)
    {
        var records = await _db.PreAuths
            .Where(p => p.OdooOrderId == odooOrderId && p.SiteCode == siteCode)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        var record = records.FirstOrDefault(r => PreAuthStateMachine.IsActive(r.Status))
            ?? records.FirstOrDefault();

        if (record is null)
        {
            _logger.LogWarning(
                "Cancel pre-auth: no record found for order {OrderId} site {Site}",
                odooOrderId, siteCode);
            return PreAuthHandlerResult.Fail(
                PreAuthHandlerError.RecordNotFound,
                $"No pre-auth found for order {odooOrderId}");
        }

        if (record.Status == PreAuthStatus.Dispensing)
        {
            _logger.LogWarning(
                "Cancel pre-auth {Id}: rejected — pump is actively dispensing",
                record.Id);
            return PreAuthHandlerResult.Fail(
                PreAuthHandlerError.CannotCancelDispensing,
                "Cannot cancel a pre-auth while the pump is actively dispensing");
        }

        if (PreAuthStateMachine.IsTerminal(record.Status))
        {
            // Already in a terminal state — idempotent return
            _logger.LogInformation(
                "Cancel pre-auth {Id}: already in terminal status {Status}", record.Id, record.Status);
            return PreAuthHandlerResult.Ok(record);
        }

        // Best-effort FCC deauthorization — for Authorized records, require confirmation
        if (record.Status == PreAuthStatus.Authorized)
        {
            var deauthorized = await TryCancelAtFccAsync(record, ct);
            if (!deauthorized)
            {
                _logger.LogWarning(
                    "Cancel pre-auth {Id}: FCC deauthorization not confirmed; keeping status as {Status}",
                    record.Id, record.Status);
                return PreAuthHandlerResult.Fail(
                    PreAuthHandlerError.FccUnreachable,
                    "FCC deauthorization could not be confirmed. The cancel will be retried.");
            }
        }
        else
        {
            // Non-authorized records (e.g. Pending): best-effort, proceed regardless
            await TryCancelAtFccAsync(record, ct);
        }

        record.Status = PreAuthStatus.Cancelled;
        record.CancelledAt = DateTimeOffset.UtcNow;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        record.IsCloudSynced = false;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Pre-auth {Id} for order {OrderId} cancelled", record.Id, odooOrderId);
        return PreAuthHandlerResult.Ok(record);
    }

    // ── RunExpiryCheckAsync ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<int> RunExpiryCheckAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var activeStatuses = PreAuthStateMachine.ActiveStatuses;

        var batchSize = _config.Value.PreAuthExpiryBatchSize;
        var expired = await _db.PreAuths
            .Where(p =>
                activeStatuses.Contains(p.Status)
                && p.ExpiresAt < now)
            .OrderBy(p => p.ExpiresAt)
            .Take(batchSize)
            .ToListAsync(ct);

        if (expired.Count == 0)
            return 0;

        _logger.LogInformation("Pre-auth expiry check: found {Count} expired record(s)", expired.Count);

        var expiredCount = 0;
        var deferredAuthorizedCount = 0;

        // P-DSK-018: Separate authorized records (need FCC deauth) from non-authorized.
        // Process FCC deauthorizations with bounded parallelism and per-record timeouts
        // to prevent blocking the cadence loop for minutes.
        var authorizedRecords = expired.Where(r => r.Status == PreAuthStatus.Authorized).ToList();
        var nonAuthorizedRecords = expired.Where(r => r.Status != PreAuthStatus.Authorized).ToList();

        // Process authorized records with bounded parallelism (max 5 concurrent FCC calls)
        if (authorizedRecords.Count > 0)
        {
            var semaphore = new SemaphoreSlim(5);
            var tasks = authorizedRecords.Select(async record =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    // P-DSK-018: Per-record timeout of 5 seconds for FCC deauth
                    using var perRecordCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    perRecordCts.CancelAfter(TimeSpan.FromSeconds(5));

                    var deauthorized = await TryCancelAtFccAsync(record, perRecordCts.Token);
                    return (record, deauthorized);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Per-record timeout — treat as deferred
                    _logger.LogWarning(
                        "FCC deauthorization timed out for pre-auth {Id}", record.Id);
                    return (record, deauthorized: false);
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);
            foreach (var (record, deauthorized) in results)
            {
                if (!deauthorized)
                {
                    deferredAuthorizedCount++;
                    _logger.LogWarning(
                        "Pre-auth {Id} remains {Status} after expiry because FCC deauthorization could not be confirmed; " +
                        "the next expiry cycle will retry",
                        record.Id, record.Status);
                    continue;
                }

                record.Status = PreAuthStatus.Expired;
                record.ExpiredAt = now;
                record.UpdatedAt = now;
                record.IsCloudSynced = false;
                expiredCount++;
            }
        }

        // Non-authorized records transition immediately
        foreach (var record in nonAuthorizedRecords)
        {
            _logger.LogInformation(
                "Expiring pre-auth {Id} (was {Status}, expired at {ExpiresAt}) for order {OrderId}",
                record.Id, record.Status, record.ExpiresAt, record.OdooOrderId);

            record.Status = PreAuthStatus.Expired;
            record.ExpiredAt = now;
            record.UpdatedAt = now;
            record.IsCloudSynced = false;
            expiredCount++;
        }

        if (expiredCount > 0)
            await _db.SaveChangesAsync(CancellationToken.None);

        _logger.LogInformation(
            "Pre-auth expiry check: transitioned {ExpiredCount} record(s) to Expired; deferred {DeferredCount} AUTHORIZED record(s) pending FCC deauthorization",
            expiredCount,
            deferredAuthorizedCount);

        return expiredCount;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to notify the FCC to release the pump authorization.
    /// Returns true when deauthorization succeeded or is not required locally.
    /// Returns false when the authorization still needs a retry (for example FCC outage).
    /// </summary>
    private async Task<bool> TryCancelAtFccAsync(PreAuthEntity record, CancellationToken ct)
    {
        if (_adapterFactory is null || record.FccCorrelationId is null)
            return true;

        if (!_connectivity.Current.IsFccUp)
        {
            _logger.LogWarning(
                "FCC deauthorization deferred for pre-auth {Id} because FCC connectivity is down",
                record.Id);
            return false;
        }

        try
        {
            var config = _config.Value;
            var resolvedConfig = DesktopFccRuntimeConfiguration.Resolve(
                config,
                _configManager?.CurrentSiteConfig,
                TimeSpan.FromSeconds(config.PreAuthTimeoutSeconds),
                record.SiteCode);
            var adapter = _adapterFactory.Create(resolvedConfig.Vendor, resolvedConfig.ConnectionConfig);

            return await adapter.CancelPreAuthAsync(record.FccCorrelationId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "FCC deauthorization failed for pre-auth {Id} (correlationId={CorrelationId})",
                record.Id, record.FccCorrelationId);
            return false;
        }
    }
}
