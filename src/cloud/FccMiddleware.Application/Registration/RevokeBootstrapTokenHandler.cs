using System.Text.Json;
using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.Registration;

public sealed class RevokeBootstrapTokenHandler
    : IRequestHandler<RevokeBootstrapTokenCommand, Result<RevokeBootstrapTokenResult>>
{
    private readonly IRegistrationDbContext _db;
    private readonly ILogger<RevokeBootstrapTokenHandler> _logger;

    public RevokeBootstrapTokenHandler(IRegistrationDbContext db, ILogger<RevokeBootstrapTokenHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<RevokeBootstrapTokenResult>> Handle(
        RevokeBootstrapTokenCommand request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var token = await _db.FindBootstrapTokenByIdAsync(request.TokenId, cancellationToken);
        if (token is null)
            return Result<RevokeBootstrapTokenResult>.Failure("TOKEN_NOT_FOUND",
                $"Bootstrap token '{request.TokenId}' not found.");

        if (token.Status == ProvisioningTokenStatus.REVOKED)
            return Result<RevokeBootstrapTokenResult>.Failure("TOKEN_ALREADY_REVOKED",
                "Bootstrap token is already revoked.");

        if (token.Status == ProvisioningTokenStatus.USED)
            return Result<RevokeBootstrapTokenResult>.Failure("TOKEN_ALREADY_USED",
                "Bootstrap token has already been used and cannot be revoked.");

        if (token.Status == ProvisioningTokenStatus.EXPIRED || token.ExpiresAt <= now)
            return Result<RevokeBootstrapTokenResult>.Failure("TOKEN_EXPIRED",
                "Bootstrap token has already expired.");

        token.Status = ProvisioningTokenStatus.REVOKED;

        _db.AddAuditEvent(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = token.LegalEntityId,
            EventType = "BOOTSTRAP_TOKEN_REVOKED",
            CorrelationId = Guid.NewGuid(),
            SiteCode = token.SiteCode,
            Source = "RevokeBootstrapTokenHandler",
            Payload = JsonSerializer.Serialize(new
            {
                TokenId = token.Id,
                SiteCode = token.SiteCode,
                RevokedBy = request.RevokedBy,
                RevokedAt = now,
            })
        });

        // L-07: Use TrySaveChangesAsync to handle concurrent revocation/registration
        // of the same token gracefully instead of producing HTTP 500.
        var saved = await _db.TrySaveChangesAsync(cancellationToken);
        if (!saved)
        {
            _logger.LogWarning("Concurrency conflict revoking bootstrap token {TokenId} — token was modified by another request",
                token.Id);
            return Result<RevokeBootstrapTokenResult>.Failure("CONCURRENCY_CONFLICT",
                "The token was modified by another operation. Please refresh and try again.");
        }

        _logger.LogInformation("Bootstrap token {TokenId} revoked for site {SiteCode} by {RevokedBy}",
            token.Id, token.SiteCode, request.RevokedBy);

        return Result<RevokeBootstrapTokenResult>.Success(new RevokeBootstrapTokenResult
        {
            TokenId = token.Id,
            RevokedAt = now
        });
    }
}
