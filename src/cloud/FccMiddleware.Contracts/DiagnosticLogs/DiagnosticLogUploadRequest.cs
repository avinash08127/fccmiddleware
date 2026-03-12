using System.ComponentModel.DataAnnotations;

namespace FccMiddleware.Contracts.DiagnosticLogs;

public sealed record DiagnosticLogUploadRequest
{
    [Required]
    public Guid DeviceId { get; init; }

    [Required]
    [StringLength(50, MinimumLength = 1)]
    public string SiteCode { get; init; } = default!;

    [Required]
    public Guid LegalEntityId { get; init; }

    [Required]
    public DateTimeOffset UploadedAtUtc { get; init; }

    /// <summary>
    /// JSONL log entries (WARN/ERROR only), max 200 entries.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public List<string> LogEntries { get; init; } = new();
}
