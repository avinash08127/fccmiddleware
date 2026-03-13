using System.ComponentModel.DataAnnotations;

namespace FccMiddleware.Contracts.Registration;

/// <summary>
/// FM-S04: Requires a reason for the irreversible decommission action.
/// </summary>
public sealed class DecommissionRequest
{
    /// <summary>
    /// Reason/justification for decommissioning the device.
    /// Recorded in the audit trail for traceability.
    /// </summary>
    [Required]
    [StringLength(500, MinimumLength = 10)]
    public string Reason { get; set; } = null!;
}
