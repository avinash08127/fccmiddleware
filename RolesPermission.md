# Roles & Permissions Implementation Plan (IMPLEMENTED)

## Current State Analysis

### Problem
Azure Entra tokens do **not** return role claims. The current codebase assumes roles arrive in the JWT `roles` claim from Entra, but this is not happening. We need to maintain roles locally in our database and resolve them at request time.

### What Exists Today

**Portal (Angular)**
- MSAL/Entra login works (authentication is fine)
- 6 hardcoded roles: `SystemAdmin`, `SystemAdministrator` (legacy), `OperationsManager`, `SiteSupervisor`, `Auditor`, `SupportReadOnly`
- Role guard (`roleGuard`) reads `idTokenClaims.roles` from Entra JWT
- `RoleVisibleDirective` for UI-level visibility
- Feature-specific role sets (reconciliation, DLQ, settings, etc.)
- **No user management screen exists**

**Cloud Backend (C#)**
- `PortalBearer` scheme validates Entra JWTs via OIDC metadata
- 3 portal authorization policies: `PortalUser`, `PortalReconciliationReview`, `PortalAdminWrite`
- `PortalAccessResolver` extracts roles from `roles` claim, legal entities from `legal_entities` claim
- `TenantScopeMiddleware` scopes queries by legal entity
- **No user/role/permission tables in database**

**Database**
- PostgreSQL with EF Core
- No `users`, `roles`, or `user_role` tables
- Multi-tenancy via `LegalEntityId` on entities + global query filters

---

## Target State: 3 Roles

| Role | Description | Key Capabilities |
|------|-------------|-----------------|
| **FCC Admin** | Full access including user management | Everything + manage users, assign roles, assign legal entities |
| **FCC User** | Operational user | View all screens, issue bootstrap tokens, approve reconciliation, manage settings, DLQ actions. **Cannot** manage users |
| **FCC Viewer** | Read-only access | View all screens (dashboard, transactions, reconciliation, agents, sites, master data, audit). **Cannot** modify anything |

### Role-to-Feature Permission Matrix

| Feature | FCC Admin | FCC User | FCC Viewer |
|---------|-----------|----------|------------|
| Dashboard (view) | Y | Y | Y |
| Transactions (view) | Y | Y | Y |
| Reconciliation (view) | Y | Y | Y |
| Reconciliation (approve/reject) | Y | Y | N |
| Edge Agents (view) | Y | Y | Y |
| Bootstrap Tokens (issue/revoke) | Y | Y | N |
| Sites (view) | Y | Y | Y |
| Sites (edit) | Y | Y | N |
| Master Data (view) | Y | Y | Y |
| Audit Log (view) | Y | Y | Y |
| DLQ (view) | Y | Y | Y |
| DLQ (retry/discard) | Y | Y | N |
| Settings (view) | Y | Y | Y |
| Settings (edit) | Y | Y | N |
| User Management | Y | N | N |
| Sensitive Data Access | Y | Y | N |

### Legal Entity Scoping
- Each user belongs to **one or more** legal entities
- An FCC Admin can optionally have `AllLegalEntities = true` (super-admin)
- All data queries are scoped to the user's assigned legal entities
- Legal entity assignment is managed via the User Management screen

---

## Implementation Plan

### Phase 1: Database — User & Role Tables

**New migration: `004-portal-users.sql`**

```sql
-- Portal user record (linked to Entra object ID)
CREATE TABLE portal_users (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    entra_object_id VARCHAR(128) NOT NULL UNIQUE,   -- Entra "oid" claim
    email           VARCHAR(320) NOT NULL,
    display_name    VARCHAR(256) NOT NULL,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by      VARCHAR(128),                    -- Entra oid of creator
    updated_by      VARCHAR(128)
);

CREATE INDEX ix_portal_users_email ON portal_users (email);

-- Role lookup (seeded, not user-editable)
CREATE TABLE portal_roles (
    id    SMALLINT PRIMARY KEY,
    name  VARCHAR(64) NOT NULL UNIQUE
);

INSERT INTO portal_roles (id, name) VALUES
    (1, 'FccAdmin'),
    (2, 'FccUser'),
    (3, 'FccViewer');

-- User ↔ Role assignment (a user has exactly one role)
CREATE TABLE portal_user_roles (
    user_id UUID NOT NULL REFERENCES portal_users(id) ON DELETE CASCADE,
    role_id SMALLINT NOT NULL REFERENCES portal_roles(id),
    PRIMARY KEY (user_id)
);

-- User ↔ Legal Entity scoping
CREATE TABLE portal_user_legal_entities (
    user_id         UUID NOT NULL REFERENCES portal_users(id) ON DELETE CASCADE,
    legal_entity_id UUID NOT NULL REFERENCES legal_entities(id),
    PRIMARY KEY (user_id, legal_entity_id)
);

-- Super-admin flag (all legal entities access)
ALTER TABLE portal_users ADD COLUMN all_legal_entities BOOLEAN NOT NULL DEFAULT FALSE;
```

**EF Core entities to add:**

| Entity | File |
|--------|------|
| `PortalUser` | `FccMiddleware.Domain/Entities/PortalUser.cs` |
| `PortalRole` | `FccMiddleware.Domain/Entities/PortalRole.cs` |
| `PortalUserLegalEntity` | `FccMiddleware.Domain/Entities/PortalUserLegalEntity.cs` |

**DbContext additions:**
```csharp
public DbSet<PortalUser> PortalUsers { get; set; }
public DbSet<PortalRole> PortalRoles { get; set; }
public DbSet<PortalUserLegalEntity> PortalUserLegalEntities { get; set; }
```

---

### Phase 2: Backend — Local Role Resolution

#### 2a. New Service: `PortalUserService`

**File:** `FccMiddleware.Api/Portal/PortalUserService.cs`

Responsibilities:
- Look up `PortalUser` by Entra `oid` claim
- Return role + legal entity list
- First-login auto-provisioning: **disabled** (admin must create user first, otherwise 403)
- Cache strategy: short TTL in-memory cache (e.g., 5 min) keyed by `oid` to avoid DB hit every request

```csharp
public sealed class PortalUserService(FccMiddlewareDbContext db, IMemoryCache cache)
{
    public async Task<PortalUserInfo?> GetByEntraOidAsync(string oid, CancellationToken ct);
    public async Task<PortalUser> CreateUserAsync(CreatePortalUserRequest req, CancellationToken ct);
    public async Task UpdateUserAsync(Guid userId, UpdatePortalUserRequest req, CancellationToken ct);
    public async Task DeactivateUserAsync(Guid userId, CancellationToken ct);
    public async Task<PagedResult<PortalUserDto>> ListUsersAsync(UserListQuery query, CancellationToken ct);
}
```

#### 2b. Middleware Change: `PortalRoleEnrichmentMiddleware`

**File:** `FccMiddleware.Api/Infrastructure/PortalRoleEnrichmentMiddleware.cs`

Runs after authentication, before authorization. For `PortalBearer`-authenticated requests:

1. Extract `oid` from JWT claims
2. Look up user in DB (via `PortalUserService`, cached)
3. If user not found or inactive → short-circuit with 403
4. Inject synthetic claims into `ClaimsPrincipal`:
   - `roles` → user's role name (e.g., `FccAdmin`)
   - `legal_entities` → comma-separated legal entity IDs (or `*`)
5. Existing `TenantScopeMiddleware` and authorization policies continue to work

This approach means **minimal changes to existing authorization policies** — they just need to check the new role names instead of the old ones.

#### 2c. Update Authorization Policies

**File:** `Program.cs` — update policy definitions

```
Old                         →  New
──────────────────────────────────────────────────────
PortalUser                  →  FccAdmin, FccUser, FccViewer  (any)
PortalReconciliationReview  →  FccAdmin, FccUser
PortalAdminWrite            →  FccAdmin, FccUser
(new) PortalUserManagement  →  FccAdmin only
```

**Updated policy definitions:**
```csharp
options.AddPolicy("PortalUser", policy => {
    policy.AddAuthenticationSchemes(PortalJwtOptions.SchemeName);
    policy.RequireAssertion(ctx => HasAnyRole(ctx, "FccAdmin", "FccUser", "FccViewer"));
});

options.AddPolicy("PortalWrite", policy => {
    policy.AddAuthenticationSchemes(PortalJwtOptions.SchemeName);
    policy.RequireAssertion(ctx => HasAnyRole(ctx, "FccAdmin", "FccUser"));
});

options.AddPolicy("PortalUserManagement", policy => {
    policy.AddAuthenticationSchemes(PortalJwtOptions.SchemeName);
    policy.RequireAssertion(ctx => HasAnyRole(ctx, "FccAdmin"));
});
```

#### 2d. Update `PortalAccessResolver`

- Replace hardcoded role sets with new 3-role model
- `HasSensitiveDataAccess`: `FccAdmin`, `FccUser` → true; `FccViewer` → false

#### 2e. New Controller: `UserManagementController`

**File:** `FccMiddleware.Api/Portal/UserManagementController.cs`

All endpoints: `[Authorize(Policy = "PortalUserManagement")]`

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/v1/admin/users` | List users (paginated, filterable by role, legal entity, status) |
| GET | `/api/v1/admin/users/{id}` | Get single user details |
| POST | `/api/v1/admin/users` | Create user (entra email, role, legal entities) |
| PUT | `/api/v1/admin/users/{id}` | Update user (role, legal entities, active status) |
| DELETE | `/api/v1/admin/users/{id}` | Deactivate user (soft delete) |
| GET | `/api/v1/admin/users/lookup?email={email}` | Look up Entra user by email (for creation) |

**Request/Response models:**

```csharp
public sealed record CreatePortalUserRequest(
    string EntraObjectId,
    string Email,
    string DisplayName,
    string Role,               // "FccAdmin" | "FccUser" | "FccViewer"
    List<Guid> LegalEntityIds,
    bool AllLegalEntities = false
);

public sealed record UpdatePortalUserRequest(
    string? Role,
    List<Guid>? LegalEntityIds,
    bool? AllLegalEntities,
    bool? IsActive
);

public sealed record PortalUserDto(
    Guid Id,
    string EntraObjectId,
    string Email,
    string DisplayName,
    string Role,
    bool AllLegalEntities,
    List<LegalEntitySummaryDto> LegalEntities,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);
```

---

### Phase 3: Portal Frontend Changes

#### 3a. Update Role Model

**File:** `src/app/core/auth/auth-state.ts`

```typescript
// Replace old roles
export type AppRole = 'FccAdmin' | 'FccUser' | 'FccViewer';

const APP_ROLES: AppRole[] = ['FccAdmin', 'FccUser', 'FccViewer'];
```

#### 3b. Change Role Source

Currently roles come from `idTokenClaims.roles`. Since Entra doesn't provide roles, the portal needs to fetch the user's role from the backend after login.

**New service:** `src/app/core/auth/portal-user.service.ts`

```typescript
@Injectable({ providedIn: 'root' })
export class PortalUserService {
  private userInfo = signal<PortalUserInfo | null>(null);

  readonly role = computed(() => this.userInfo()?.role ?? null);
  readonly legalEntities = computed(() => this.userInfo()?.legalEntities ?? []);
  readonly isAdmin = computed(() => this.role() === 'FccAdmin');

  /** Called once after MSAL login succeeds */
  async loadCurrentUser(): Promise<void> {
    // GET /api/v1/admin/users/me
    // Stores result in userInfo signal
  }
}
```

**New backend endpoint:** `GET /api/v1/admin/users/me`
- Returns current user's role + legal entities (accessible by any authenticated Entra user)
- If user doesn't exist in DB → 403 with message "Account not provisioned. Contact your FCC Admin."

#### 3c. Update Auth Initialization

**File:** `src/app/core/auth/auth-state.ts`

After MSAL login success:
1. Call `PortalUserService.loadCurrentUser()`
2. If 403 → redirect to "not provisioned" page
3. If success → store role in signal, proceed to app

#### 3d. Update Guards & Directives

**File:** `src/app/core/auth/role.guard.ts`
- Change to read role from `PortalUserService.role()` signal instead of JWT claims

**File:** `src/app/shared/directives/role-visible/role-visible.directive.ts`
- Change to read role from `PortalUserService.role()` signal

#### 3e. Update Route Permissions

| Feature | Route | Required Roles |
|---------|-------|---------------|
| Dashboard | `/dashboard` | FccAdmin, FccUser, FccViewer |
| Transactions | `/transactions` | FccAdmin, FccUser, FccViewer |
| Reconciliation (view) | `/reconciliation` | FccAdmin, FccUser, FccViewer |
| Reconciliation (approve/reject) | UI button visibility | FccAdmin, FccUser |
| Edge Agents | `/agents` | FccAdmin, FccUser, FccViewer |
| Bootstrap Tokens | `/agents/bootstrap-token` | FccAdmin, FccUser |
| Sites | `/sites` | FccAdmin, FccUser, FccViewer |
| Master Data | `/master-data` | FccAdmin, FccUser, FccViewer |
| Audit Log | `/audit` | FccAdmin, FccUser, FccViewer |
| DLQ | `/dlq` | FccAdmin, FccUser, FccViewer |
| DLQ (retry/discard) | UI button visibility | FccAdmin, FccUser |
| Settings | `/settings` | FccAdmin, FccUser, FccViewer |
| Settings (edit) | UI button visibility | FccAdmin, FccUser |
| **User Management** | `/admin/users` | **FccAdmin** |

#### 3f. Update Feature Role Constants

Replace all feature-specific role arrays:

```typescript
// reconciliation.roles.ts
export const RECONCILIATION_VIEW_ROLES: AppRole[] = ['FccAdmin', 'FccUser', 'FccViewer'];
export const RECONCILIATION_WRITE_ROLES: AppRole[] = ['FccAdmin', 'FccUser'];

// Common reusable sets
export const ALL_ROLES: AppRole[] = ['FccAdmin', 'FccUser', 'FccViewer'];
export const WRITE_ROLES: AppRole[] = ['FccAdmin', 'FccUser'];
export const ADMIN_ROLES: AppRole[] = ['FccAdmin'];
```

#### 3g. New Feature: User Management Screen

**Location:** `src/app/features/user-management/`

**Components:**

| Component | Description |
|-----------|-------------|
| `user-list.component.ts` | Paginated table of users with role/status filters |
| `user-create-dialog.component.ts` | Dialog to create new user (email lookup, role select, legal entity multi-select) |
| `user-edit-dialog.component.ts` | Dialog to edit user role, legal entities, active status |

**User List Table Columns:**
- Display Name
- Email
- Role (badge)
- Legal Entities (chips/tags)
- Status (Active/Inactive badge)
- Last Updated
- Actions (Edit, Deactivate/Reactivate)

**Create User Flow:**
1. Admin enters email address
2. Portal calls `GET /api/v1/admin/users/lookup?email={email}` to resolve Entra user (display name, oid)
3. Admin selects role from dropdown (FccAdmin, FccUser, FccViewer)
4. Admin selects legal entities from multi-select (checkboxes) or toggles "All Legal Entities"
5. Submit → `POST /api/v1/admin/users`
6. New user can now log in and will be recognized

**Edit User Flow:**
1. Admin clicks Edit on user row
2. Dialog pre-fills current role, legal entities, status
3. Admin modifies as needed
4. Submit → `PUT /api/v1/admin/users/{id}`

**Navigation:**
- Add "User Management" item to shell sidebar/nav
- Only visible to FccAdmin via `RoleVisibleDirective`

---

### Phase 4: Seeding the First Admin

**Bootstrap problem:** No users exist in `portal_users` table initially, so no one can access user management to create users.

**Solution — Seed migration + CLI fallback:**

1. **Migration seed:** `004-portal-users.sql` includes an `INSERT` for the initial admin user using a known Entra `oid` (configured per environment)
2. **CLI/script fallback:** A one-time script/endpoint to seed the first admin:
   ```bash
   dotnet run -- seed-admin --oid "entra-object-id" --email "admin@company.com" --name "Initial Admin"
   ```
3. **Auto-provision flag** (optional, dev-only): Environment variable `FCC_AUTO_PROVISION_FIRST_ADMIN=true` — if the DB has zero portal users and someone logs in via Entra, they are automatically made FccAdmin. Disabled in production.

---

## File Change Summary

### New Files

| File | Description |
|------|-------------|
| `db/migrations/004-portal-users.sql` | Migration for portal_users, portal_roles, portal_user_roles, portal_user_legal_entities |
| `src/cloud/FccMiddleware.Domain/Entities/PortalUser.cs` | User entity |
| `src/cloud/FccMiddleware.Domain/Entities/PortalRole.cs` | Role entity |
| `src/cloud/FccMiddleware.Domain/Entities/PortalUserLegalEntity.cs` | Join entity |
| `src/cloud/FccMiddleware.Api/Infrastructure/PortalRoleEnrichmentMiddleware.cs` | Enriches ClaimsPrincipal with DB roles |
| `src/cloud/FccMiddleware.Api/Portal/PortalUserService.cs` | User CRUD + lookup logic |
| `src/cloud/FccMiddleware.Api/Portal/UserManagementController.cs` | Admin endpoints for user management |
| `src/cloud/FccMiddleware.Api/Portal/UserManagementModels.cs` | Request/response DTOs |
| `src/portal/src/app/core/auth/portal-user.service.ts` | Frontend service fetching user role from backend |
| `src/portal/src/app/features/user-management/user-management.routes.ts` | Feature routes |
| `src/portal/src/app/features/user-management/user-list.component.ts` | User list page |
| `src/portal/src/app/features/user-management/user-create-dialog.component.ts` | Create user dialog |
| `src/portal/src/app/features/user-management/user-edit-dialog.component.ts` | Edit user dialog |
| `src/portal/src/app/features/user-management/user-management.service.ts` | API client for user endpoints |

### Modified Files

| File | Change |
|------|--------|
| `src/cloud/FccMiddleware.Infrastructure/Persistence/FccMiddlewareDbContext.cs` | Add DbSets for PortalUser, PortalRole, PortalUserLegalEntity |
| `src/cloud/FccMiddleware.Api/Program.cs` | Add PortalRoleEnrichmentMiddleware, update policies, add UserManagement policy, register PortalUserService |
| `src/cloud/FccMiddleware.Api/Portal/PortalAccessResolver.cs` | Update role names (FccAdmin/FccUser/FccViewer), update sensitive data roles |
| `src/portal/src/app/core/auth/auth-state.ts` | Replace AppRole type, change role source from JWT to backend |
| `src/portal/src/app/core/auth/role.guard.ts` | Read role from PortalUserService instead of JWT claims |
| `src/portal/src/app/shared/directives/role-visible/role-visible.directive.ts` | Read role from PortalUserService |
| `src/portal/src/app/core/layout/shell.component.ts` | Add User Management nav item (FccAdmin only), update role display |
| `src/portal/src/app/app.routes.ts` | Add user-management route |
| `src/portal/src/app/features/reconciliation/reconciliation.roles.ts` | Update to new role names |
| `src/portal/src/app/features/edge-agents/edge-agents.routes.ts` | Update to new role names |
| `src/portal/src/app/features/site-config/site-config.routes.ts` | Update to new role names |
| `src/portal/src/app/features/dlq/dlq.routes.ts` | Update to new role names |
| `src/portal/src/app/features/settings/settings.routes.ts` | Update to new role names |
| `src/portal/src/app/features/master-data/master-data.routes.ts` | Update to new role names |
| `cypress/e2e/role-access.cy.ts` | Update to test new 3-role model |

---

## Implementation Order

1. **Database migration** — Create tables, seed roles
2. **Domain entities** — PortalUser, PortalRole, PortalUserLegalEntity
3. **DbContext update** — Add DbSets + configuration
4. **PortalUserService** — CRUD + caching
5. **PortalRoleEnrichmentMiddleware** — Inject roles into ClaimsPrincipal
6. **Update Program.cs** — Register middleware, update policies, add PortalUserManagement policy
7. **UserManagementController** — API endpoints
8. **Update PortalAccessResolver** — New role names
9. **Seed first admin** — Migration or CLI
10. **Portal: PortalUserService** — Fetch role from backend
11. **Portal: Update auth-state, guards, directives** — New role source
12. **Portal: Update all feature role constants** — New role names
13. **Portal: User Management screen** — List, create, edit components
14. **Portal: Navigation update** — Add User Management link
15. **E2E tests** — Update for new 3-role model
