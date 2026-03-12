using FccDesktopAgent.Core.Security;

namespace FccDesktopAgent.Core.PreAuth;

/// <summary>
/// Pre-authorization request received from Odoo POS over the local LAN.
/// Contains Odoo-facing pump/nozzle numbers that the handler translates to FCC numbers
/// via <c>NozzleMapping</c> before forwarding to the FCC adapter.
/// </summary>
public sealed record OdooPreAuthRequest(
    /// <summary>Odoo order identifier. Forms idempotency key with SiteCode.</summary>
    string OdooOrderId,

    /// <summary>Site code for this agent's site.</summary>
    string SiteCode,

    /// <summary>Odoo pump number (translated to FCC pump number via NozzleMapping).</summary>
    int OdooPumpNumber,

    /// <summary>Odoo nozzle number (translated to FCC nozzle number via NozzleMapping).</summary>
    int OdooNozzleNumber,

    /// <summary>Requested authorization amount in minor currency units (e.g. cents).</summary>
    long RequestedAmountMinorUnits,

    /// <summary>Unit price in minor units per litre at time of pre-auth.</summary>
    long UnitPriceMinorPerLitre,

    /// <summary>ISO 4217 currency code (e.g. "ETB").</summary>
    string Currency,

    string? VehicleNumber = null,

    [SensitiveData] string? CustomerName = null,

    [SensitiveData] string? CustomerTaxId = null,

    string? CustomerBusinessName = null,

    string? AttendantId = null);
