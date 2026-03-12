using System.Security.Cryptography;
using System.Text.Json;
using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.Registration;

public sealed class GenerateBootstrapTokenHandler
    : IRequestHandler<GenerateBootstrapTokenCommand, Result<GenerateBootstrapTokenResult>>
{
    internal const int MaxActiveTokensPerSite = 5;

    private readonly IRegistrationDbContext _db;
    private readonly ILogger<GenerateBootstrapTokenHandler> _logger;

    public GenerateBootstrapTokenHandler(IRegistrationDbContext db, ILogger<GenerateBootstrapTokenHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<GenerateBootstrapTokenResult>> Handle(
        GenerateBootstrapTokenCommand request, CancellationToken cancellationToken)
    {
        // Validate site exists
        var site = await _db.FindSiteBySiteCodeAsync(request.SiteCode, cancellationToken);
        if (site is null)
            return Result<GenerateBootstrapTokenResult>.Failure("SITE_NOT_FOUND",
                $"Site '{request.SiteCode}' not found.");

        if (site.LegalEntityId != request.LegalEntityId)
            return Result<GenerateBootstrapTokenResult>.Failure("SITE_ENTITY_MISMATCH",
                "Site does not belong to the specified legal entity.");

        // Enforce active bootstrap token limit per site
        var activeCount = await _db.CountActiveBootstrapTokensForSiteAsync(
            request.SiteCode, request.LegalEntityId, cancellationToken);
        if (activeCount >= MaxActiveTokensPerSite)
            return Result<GenerateBootstrapTokenResult>.Failure("ACTIVE_TOKEN_LIMIT_REACHED",
                $"Site '{request.SiteCode}' already has {activeCount} active bootstrap token(s). " +
                $"Maximum allowed is {MaxActiveTokensPerSite}. Revoke or wait for existing tokens to expire before generating new ones.");

        // Generate 32-byte random token, Base64URL-encoded
        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Base64UrlEncode(rawBytes);
        var tokenHash = ComputeSha256Hex(rawToken);

        var expiresAt = DateTimeOffset.UtcNow.AddHours(72);
        var now = DateTimeOffset.UtcNow;

        var token = new BootstrapToken
        {
            Id = Guid.NewGuid(),
            LegalEntityId = request.LegalEntityId,
            SiteCode = request.SiteCode,
            TokenHash = tokenHash,
            Status = ProvisioningTokenStatus.ACTIVE,
            CreatedBy = request.CreatedBy,
            ExpiresAt = expiresAt,
            CreatedAt = now,
            Environment = request.Environment
        };

        _db.AddBootstrapToken(token);

        // Audit: bootstrap token generation
        _db.AddAuditEvent(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = request.LegalEntityId,
            EventType = "BOOTSTRAP_TOKEN_GENERATED",
            CorrelationId = Guid.NewGuid(),
            SiteCode = request.SiteCode,
            Source = "GenerateBootstrapTokenHandler",
            Payload = JsonSerializer.Serialize(new
            {
                TokenId = token.Id,
                SiteCode = request.SiteCode,
                CreatedBy = request.CreatedBy,
                ExpiresAt = expiresAt,
            })
        });

        await _db.SaveChangesAsync(cancellationToken);

        // L-06: Re-check active count after save to detect TOCTOU race.
        // Two concurrent requests can both pass the initial check; this secondary
        // check detects and logs when the limit was exceeded by a race.
        var postSaveCount = await _db.CountActiveBootstrapTokensForSiteAsync(
            request.SiteCode, request.LegalEntityId, cancellationToken);
        if (postSaveCount > MaxActiveTokensPerSite)
        {
            _logger.LogWarning(
                "Bootstrap token limit exceeded due to concurrent insertion for site {SiteCode} " +
                "(count={Count}, limit={Limit}). Token was created but limit is soft-breached.",
                request.SiteCode, postSaveCount, MaxActiveTokensPerSite);
        }

        _logger.LogInformation("Bootstrap token generated for site {SiteCode}, expires at {ExpiresAt}",
            request.SiteCode, expiresAt);

        return Result<GenerateBootstrapTokenResult>.Success(new GenerateBootstrapTokenResult
        {
            TokenId = token.Id,
            RawToken = rawToken,
            ExpiresAt = expiresAt
        });
    }

    internal static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal static string ComputeSha256Hex(string input)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
