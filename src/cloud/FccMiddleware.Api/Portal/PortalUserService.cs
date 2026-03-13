using FccMiddleware.Domain.Entities;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FccMiddleware.Api.Portal;

/// <summary>
/// Manages portal user CRUD and provides cached lookups by email.
/// </summary>
public sealed class PortalUserService
{
    private readonly FccMiddlewareDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PortalUserService> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public PortalUserService(
        FccMiddlewareDbContext db,
        IMemoryCache cache,
        ILogger<PortalUserService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Look up a portal user by email (case-insensitive). Returns null if not provisioned.
    /// If oid is provided and the user record doesn't have one yet, back-fills it.
    /// Result is cached for 5 minutes.
    /// </summary>
    public async Task<PortalUserInfo?> GetByEmailAsync(string email, string? oid, CancellationToken ct)
    {
        var normalizedEmail = email.ToLowerInvariant();
        var cacheKey = $"portal_user:{normalizedEmail}";
        if (_cache.TryGetValue(cacheKey, out PortalUserInfo? cached))
            return cached;

        var user = await _db.PortalUsers
            .Include(u => u.Role)
            .Include(u => u.LegalEntityLinks)
            .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail && u.IsActive, ct);

        if (user is null)
            return null;

        // Back-fill Entra oid on first login if not already set.
        if (!string.IsNullOrEmpty(oid) && string.IsNullOrEmpty(user.EntraObjectId))
        {
            user.EntraObjectId = oid;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Back-filled Entra oid for user {Email}", email);
        }

        // Detach so the cached object is read-only
        _db.Entry(user).State = EntityState.Detached;
        foreach (var link in user.LegalEntityLinks)
            _db.Entry(link).State = EntityState.Detached;

        var info = MapToInfo(user);
        _cache.Set(cacheKey, info, CacheTtl);
        return info;
    }

    public async Task<PortalUserInfo?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var user = await _db.PortalUsers
            .AsNoTracking()
            .Include(u => u.Role)
            .Include(u => u.LegalEntityLinks)
                .ThenInclude(l => l.LegalEntity)
            .FirstOrDefaultAsync(u => u.Id == id, ct);

        return user is null ? null : MapToInfo(user);
    }

    public async Task<PortalUser> CreateUserAsync(
        string email,
        string displayName,
        string roleName,
        List<Guid> legalEntityIds,
        bool allLegalEntities,
        string createdBy,
        CancellationToken ct)
    {
        var role = await _db.PortalRoles
            .FirstOrDefaultAsync(r => r.Name == roleName, ct)
            ?? throw new ArgumentException($"Unknown role: {roleName}");

        var user = new PortalUser
        {
            Id = Guid.NewGuid(),
            Email = email.ToLowerInvariant(),
            DisplayName = displayName,
            RoleId = role.Id,
            AllLegalEntities = allLegalEntities,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CreatedBy = createdBy,
            UpdatedBy = createdBy,
        };

        foreach (var leId in legalEntityIds)
        {
            user.LegalEntityLinks.Add(new PortalUserLegalEntity
            {
                UserId = user.Id,
                LegalEntityId = leId,
            });
        }

        _db.PortalUsers.Add(user);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Portal user created: {Email} with role {Role}", email, roleName);
        return user;
    }

    public async Task<bool> UpdateUserAsync(
        Guid userId,
        string? roleName,
        List<Guid>? legalEntityIds,
        bool? allLegalEntities,
        bool? isActive,
        string updatedBy,
        CancellationToken ct)
    {
        var user = await _db.PortalUsers
            .Include(u => u.LegalEntityLinks)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return false;

        if (roleName is not null)
        {
            var role = await _db.PortalRoles
                .FirstOrDefaultAsync(r => r.Name == roleName, ct)
                ?? throw new ArgumentException($"Unknown role: {roleName}");
            user.RoleId = role.Id;
        }

        if (allLegalEntities.HasValue)
            user.AllLegalEntities = allLegalEntities.Value;

        if (isActive.HasValue)
            user.IsActive = isActive.Value;

        if (legalEntityIds is not null)
        {
            user.LegalEntityLinks.Clear();
            foreach (var leId in legalEntityIds)
            {
                user.LegalEntityLinks.Add(new PortalUserLegalEntity
                {
                    UserId = userId,
                    LegalEntityId = leId,
                });
            }
        }

        user.UpdatedAt = DateTimeOffset.UtcNow;
        user.UpdatedBy = updatedBy;

        await _db.SaveChangesAsync(ct);

        // Invalidate cache
        _cache.Remove($"portal_user:{user.Email.ToLowerInvariant()}");
        _logger.LogInformation("Portal user updated: {UserId}", userId);
        return true;
    }

    public async Task<(List<PortalUserDto> Items, int TotalCount)> ListUsersAsync(
        string? roleFilter,
        Guid? legalEntityFilter,
        bool? activeFilter,
        string? searchTerm,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var query = _db.PortalUsers
            .AsNoTracking()
            .Include(u => u.Role)
            .Include(u => u.LegalEntityLinks)
                .ThenInclude(l => l.LegalEntity)
            .AsQueryable();

        if (roleFilter is not null)
            query = query.Where(u => u.Role.Name == roleFilter);

        if (activeFilter.HasValue)
            query = query.Where(u => u.IsActive == activeFilter.Value);

        if (legalEntityFilter.HasValue)
            query = query.Where(u =>
                u.AllLegalEntities
                || u.LegalEntityLinks.Any(l => l.LegalEntityId == legalEntityFilter.Value));

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.ToLower();
            query = query.Where(u =>
                u.Email.ToLower().Contains(term)
                || u.DisplayName.ToLower().Contains(term));
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderBy(u => u.DisplayName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new PortalUserDto
            {
                Id = u.Id,
                Email = u.Email,
                DisplayName = u.DisplayName,
                Role = u.Role.Name,
                AllLegalEntities = u.AllLegalEntities,
                IsActive = u.IsActive,
                CreatedAt = u.CreatedAt,
                UpdatedAt = u.UpdatedAt,
                LegalEntities = u.LegalEntityLinks.Select(l => new LegalEntitySummaryDto
                {
                    Id = l.LegalEntityId,
                    Name = l.LegalEntity.Name,
                    CountryCode = l.LegalEntity.CountryCode,
                }).ToList(),
            })
            .ToListAsync(ct);

        return (items, totalCount);
    }

    private static PortalUserInfo MapToInfo(PortalUser user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        DisplayName = user.DisplayName,
        RoleName = user.Role.Name,
        AllLegalEntities = user.AllLegalEntities,
        LegalEntityIds = user.LegalEntityLinks.Select(l => l.LegalEntityId).ToList(),
        LegalEntities = user.LegalEntityLinks
            .Where(l => l.LegalEntity is not null)
            .Select(l => new LegalEntitySummaryDto
            {
                Id = l.LegalEntityId,
                Name = l.LegalEntity.Name,
                CountryCode = l.LegalEntity.CountryCode,
            }).ToList(),
    };
}

public sealed class PortalUserInfo
{
    public Guid Id { get; init; }
    public string Email { get; init; } = null!;
    public string DisplayName { get; init; } = null!;
    public string RoleName { get; init; } = null!;
    public bool AllLegalEntities { get; init; }
    public List<Guid> LegalEntityIds { get; init; } = [];
    public List<LegalEntitySummaryDto> LegalEntities { get; init; } = [];
}

public sealed class PortalUserDto
{
    public Guid Id { get; init; }
    public string Email { get; init; } = null!;
    public string DisplayName { get; init; } = null!;
    public string Role { get; init; } = null!;
    public bool AllLegalEntities { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public List<LegalEntitySummaryDto> LegalEntities { get; init; } = [];
}

public sealed class LegalEntitySummaryDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = null!;
    public string CountryCode { get; init; } = null!;
}
