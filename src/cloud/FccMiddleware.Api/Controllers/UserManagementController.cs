using FccMiddleware.Api.Portal;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FccMiddleware.Api.Controllers;

/// <summary>
/// CRUD endpoints for managing portal users, roles, and legal entity assignments.
/// Restricted to FccAdmin users only.
/// </summary>
[ApiController]
[Route("api/v1/admin/users")]
[Authorize(Policy = "PortalUserManagement")]
public sealed class UserManagementController : ControllerBase
{
    private readonly PortalUserService _userService;
    private readonly FccMiddlewareDbContext _db;
    private readonly ILogger<UserManagementController> _logger;

    public UserManagementController(
        PortalUserService userService,
        FccMiddlewareDbContext db,
        ILogger<UserManagementController> logger)
    {
        _userService = userService;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Get the current authenticated user's profile (accessible by any portal user).
    /// </summary>
    [HttpGet("me")]
    [Authorize(Policy = "PortalUser")]
    public async Task<IActionResult> GetCurrentUser(CancellationToken ct)
    {
        var email = ResolveEmail();
        if (email is null)
            return Unauthorized(new { errorCode = "NO_EMAIL", message = "Missing email claim." });

        var userInfo = await _userService.GetByEmailAsync(email, oid: null, ct);
        if (userInfo is null)
            return StatusCode(403, new
            {
                errorCode = "USER_NOT_PROVISIONED",
                message = "Your account has not been provisioned. Contact your FCC Admin.",
            });

        return Ok(new
        {
            userInfo.Id,
            userInfo.Email,
            userInfo.DisplayName,
            role = userInfo.RoleName,
            userInfo.AllLegalEntities,
            legalEntities = userInfo.LegalEntities,
        });
    }

    /// <summary>List all portal users with filtering and pagination.</summary>
    [HttpGet]
    public async Task<IActionResult> ListUsers(
        [FromQuery] string? role,
        [FromQuery] Guid? legalEntityId,
        [FromQuery] bool? isActive,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        var (items, totalCount) = await _userService.ListUsersAsync(
            role, legalEntityId, isActive, search, page, pageSize, ct);

        return Ok(new
        {
            items,
            totalCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
        });
    }

    /// <summary>Get a single user by ID.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetUser(Guid id, CancellationToken ct)
    {
        var userInfo = await _userService.GetByIdAsync(id, ct);
        if (userInfo is null)
            return NotFound(new { errorCode = "USER_NOT_FOUND", message = "User not found." });

        return Ok(new
        {
            userInfo.Id,
            userInfo.Email,
            userInfo.DisplayName,
            role = userInfo.RoleName,
            userInfo.AllLegalEntities,
            legalEntities = userInfo.LegalEntities,
        });
    }

    /// <summary>Create a new portal user.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreatePortalUserRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new { errorCode = "VALIDATION", message = "email is required." });
        if (string.IsNullOrWhiteSpace(request.Role))
            return BadRequest(new { errorCode = "VALIDATION", message = "role is required." });

        var validRoles = new[] { "FccAdmin", "FccUser", "FccViewer" };
        if (!validRoles.Contains(request.Role, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { errorCode = "VALIDATION", message = $"Invalid role. Must be one of: {string.Join(", ", validRoles)}" });

        // Check for existing user with same email
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var existing = await _db.PortalUsers
            .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail, ct);
        if (existing is not null)
            return Conflict(new { errorCode = "USER_EXISTS", message = "A user with this email already exists.", existingUserId = existing.Id });

        var callerEmail = ResolveEmail() ?? "unknown";

        try
        {
            var user = await _userService.CreateUserAsync(
                request.Email,
                request.DisplayName ?? request.Email,
                request.Role,
                request.LegalEntityIds ?? [],
                request.AllLegalEntities,
                callerEmail,
                ct);

            _logger.LogInformation(
                "Admin {Caller} created portal user {UserId} ({Email}) with role {Role}",
                callerEmail, user.Id, request.Email, request.Role);

            return Created($"/api/v1/admin/users/{user.Id}", new { id = user.Id });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { errorCode = "VALIDATION", message = ex.Message });
        }
    }

    /// <summary>Update a portal user's role, legal entities, or active status.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdatePortalUserRequest request, CancellationToken ct)
    {
        if (request.Role is not null)
        {
            var validRoles = new[] { "FccAdmin", "FccUser", "FccViewer" };
            if (!validRoles.Contains(request.Role, StringComparer.OrdinalIgnoreCase))
                return BadRequest(new { errorCode = "VALIDATION", message = $"Invalid role. Must be one of: {string.Join(", ", validRoles)}" });
        }

        var callerEmail = ResolveEmail() ?? "unknown";

        try
        {
            var updated = await _userService.UpdateUserAsync(
                id, request.Role, request.LegalEntityIds, request.AllLegalEntities,
                request.IsActive, callerEmail, ct);

            if (!updated)
                return NotFound(new { errorCode = "USER_NOT_FOUND", message = "User not found." });

            _logger.LogInformation("Admin {Caller} updated portal user {UserId}", callerEmail, id);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { errorCode = "VALIDATION", message = ex.Message });
        }
    }

    /// <summary>Deactivate a portal user (soft delete).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeactivateUser(Guid id, CancellationToken ct)
    {
        var callerEmail = ResolveEmail() ?? "unknown";
        var updated = await _userService.UpdateUserAsync(
            id, null, null, null, isActive: false, callerEmail, ct);

        if (!updated)
            return NotFound(new { errorCode = "USER_NOT_FOUND", message = "User not found." });

        _logger.LogInformation("Admin {Caller} deactivated portal user {UserId}", callerEmail, id);
        return NoContent();
    }

    /// <summary>List all available legal entities for user assignment.</summary>
    [HttpGet("legal-entities")]
    public async Task<IActionResult> ListLegalEntities(CancellationToken ct)
    {
        var entities = await _db.LegalEntities
            .AsNoTracking()
            .Where(le => le.IsActive)
            .OrderBy(le => le.Name)
            .Select(le => new LegalEntitySummaryDto
            {
                Id = le.Id,
                Name = le.Name,
                CountryCode = le.CountryCode,
            })
            .ToListAsync(ct);

        return Ok(entities);
    }

    /// <summary>List available roles.</summary>
    [HttpGet("roles")]
    public async Task<IActionResult> ListRoles(CancellationToken ct)
    {
        var roles = await _db.PortalRoles
            .AsNoTracking()
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(ct);

        return Ok(roles);
    }

    private string? ResolveEmail() =>
        User.FindFirst("preferred_username")?.Value
        ?? User.FindFirst("email")?.Value
        ?? User.FindFirst("emails")?.Value
        ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
}

public sealed record CreatePortalUserRequest(
    string Email,
    string? DisplayName,
    string Role,
    List<Guid>? LegalEntityIds,
    bool AllLegalEntities = false);

public sealed record UpdatePortalUserRequest(
    string? Role = null,
    List<Guid>? LegalEntityIds = null,
    bool? AllLegalEntities = null,
    bool? IsActive = null);
