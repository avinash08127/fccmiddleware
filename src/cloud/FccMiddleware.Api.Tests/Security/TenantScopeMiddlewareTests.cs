using System.Security.Claims;
using FccMiddleware.Api.Infrastructure;

namespace FccMiddleware.Api.Tests.Security;

public sealed class TenantScopeMiddlewareTests
{
    [Fact]
    public void TryResolveTenant_UsesLeiClaim_WhenPresent()
    {
        var tenantId = Guid.Parse("99000000-0000-0000-0000-000000000021");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("lei", tenantId.ToString())
        ]));

        var resolved = TenantScopeMiddleware.TryResolveTenant(principal, out var result);

        resolved.Should().BeTrue();
        result.Should().Be(tenantId);
    }

    [Fact]
    public void TryResolveTenant_UsesSingleLegalEntitiesClaim_WhenLeiAbsent()
    {
        var tenantId = Guid.Parse("99000000-0000-0000-0000-000000000022");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("legal_entities", tenantId.ToString())
        ]));

        var resolved = TenantScopeMiddleware.TryResolveTenant(principal, out var result);

        resolved.Should().BeTrue();
        result.Should().Be(tenantId);
    }

    [Fact]
    public void TryResolveTenant_DoesNotResolveMultipleConcreteTenants()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("legal_entities", "99000000-0000-0000-0000-000000000023,99000000-0000-0000-0000-000000000024")
        ]));

        var resolved = TenantScopeMiddleware.TryResolveTenant(principal, out var result);

        resolved.Should().BeFalse();
        result.Should().Be(Guid.Empty);
    }
}
