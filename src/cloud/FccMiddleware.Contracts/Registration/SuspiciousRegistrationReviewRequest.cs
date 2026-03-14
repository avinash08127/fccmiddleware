using System.ComponentModel.DataAnnotations;

namespace FccMiddleware.Contracts.Registration;

public sealed class SuspiciousRegistrationReviewRequest
{
    [Required]
    [StringLength(500, MinimumLength = 10)]
    public string Reason { get; set; } = null!;
}
