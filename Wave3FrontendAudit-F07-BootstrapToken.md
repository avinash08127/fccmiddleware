# F-07 Bootstrap Token Page — Audit Report

**Page:** `/agents/bootstrap-token` — `BootstrapTokenComponent`
**Audited:** 2026-03-13
**Status:** Issues Found

---

## 1. File Inventory

| File | Purpose |
|------|---------|
| `src/portal/src/app/features/edge-agents/bootstrap-token.component.ts` | Token generation form — legal entity + site selection, QR code generation, copy/reveal/revoke UX |
| `src/portal/src/app/features/edge-agents/edge-agents.routes.ts` | Route definition with `roleGuard(['SystemAdmin'])` |
| `src/portal/src/app/core/services/bootstrap-token.service.ts` | HTTP service — `generate()` and `revoke()` |
| `src/portal/src/app/core/models/bootstrap-token.model.ts` | TypeScript interfaces — request/response shapes |
| `src/portal/src/app/core/services/master-data.service.ts` | `getLegalEntities()` — populates legal entity dropdown |
| `src/portal/src/app/core/services/site.service.ts` | `getSites()` — populates site dropdown on entity selection |
| `src/environments/environment.ts` | `apiBaseUrl` — embedded in QR payload |
| `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs` | Backend endpoints: `POST /api/v1/admin/bootstrap-tokens`, `DELETE /api/v1/admin/bootstrap-tokens/{tokenId}` |
| `src/cloud/FccMiddleware.Application/Registration/GenerateBootstrapTokenHandler.cs` | Command handler — token generation, TOCTOU guard, audit event |
| `src/cloud/FccMiddleware.Application/Registration/RevokeBootstrapTokenHandler.cs` | Command handler — token revocation, concurrency guard, audit event |
| `src/cloud/FccMiddleware.Domain/Entities/BootstrapToken.cs` | Domain entity |
| `src/cloud/FccMiddleware.Contracts/Registration/GenerateBootstrapTokenRequest.cs` | Backend request DTO |
| `src/cloud/FccMiddleware.Contracts/Registration/GenerateBootstrapTokenResponse.cs` | Backend response DTO (RawToken marked `[Sensitive]`) |

---

## 2. Routing & Auth

- **Route:** defined in `edge-agents.routes.ts` line 11–16: `path: 'bootstrap-token'`, lazy-loads `BootstrapTokenComponent`.
- **Frontend guard:** `canActivate: [roleGuard(['SystemAdmin'])]` — correctly restricts to SystemAdmin only.
- **Backend policy:** Both endpoints use `[Authorize(Policy = "PortalAdminWrite")]` — requires admin-level roles with write access.
- **Backend access resolver:** Controller checks `access.CanAccess(request.LegalEntityId)` for generate, and `access.CanAccess(token.LegalEntityId)` for revoke — ensures legal-entity scoping.

**No routing/auth issues.** The frontend guard and backend policy are consistent and restrictive.

---

## 3. UI Logic Review

### 3a. Component Structure

- Standalone component with Angular signals.
- Two-step form: (1) select legal entity → (2) select site, choose optional environment → generate token.
- After generation: displays token ID, expiry, masked token, copy/reveal/revoke buttons, QR code with download/print.
- **M-17 pattern implemented:** Raw token stored in a plain `private` field (`rawTokenValue`), not in a signal (to reduce DevTools exposure). The `generatedToken` signal stores metadata with `rawToken: ''`. On copy, `rawTokenValue` is nulled out and `tokenCleared` flag set.

### 3b. Data Loading

- Constructor subscribes to `masterDataService.getLegalEntities()` with `takeUntilDestroyed()` — correct cleanup.
- On legal entity change: resets site, loads sites via `siteService.getSites({ legalEntityId, pageSize: 500, isActive: true })`.
- `catchError` on site load returns `EMPTY` but does not surface error to user.

### 3c. Token Generation Flow

1. Validates `legalEntityId` and `siteCode` are non-null before calling API.
2. Resets all generation state signals (error, token, QR, revoked, etc.).
3. Calls `bootstrapTokenService.generate(req)` — POST to `/api/v1/admin/bootstrap-tokens`.
4. On success: stores raw token separately, strips it from the signal, generates QR code.
5. Error handling: discriminates HTTP 0 (network), 403 (permission), 422/400 (validation), and generic errors.

### 3d. QR Code Generation

- Uses `qrcode` library to generate a data URL.
- QR payload is a JSON object: `{ v, sc, cu, pt }` where `cu` = `environment.apiBaseUrl` and `pt` = raw token.
- Version field: `v: 2` when environment is selected, `v: 1` otherwise. Environment key added as `env`.
- Error correction level 'H' (high), 560px width.
- **QR generation is async but failure is silently swallowed** — token remains usable via copy.

### 3e. Copy & Memory Clearing

- `copyToken()` uses `navigator.clipboard.writeText()`.
- On success: clears `rawTokenValue`, sets `tokenCleared`, resets `tokenRevealed`.
- On failure: catches silently — user can still reveal and manually copy.
- `tokenDisplay` computed: shows `(cleared from memory)` after copy, masked form otherwise (first 6 + last 4 chars).

### 3f. Revoke Flow

- Calls `bootstrapTokenService.revoke(token.tokenId)` — DELETE to `/api/v1/admin/bootstrap-tokens/{tokenId}`.
- On success: sets `tokenRevoked` flag, clears QR code.
- On failure: shows error message.
- **Revoke button hidden once revoked** — correct.

### 3g. Print & Download

- `downloadQr()`: creates temporary `<a>` element, triggers download of QR data URL as PNG.
- `printQr()`: opens new window via `window.open()`, writes inline HTML with QR image, calls `win.print()`.

---

## 4. Validations Review

### Frontend Validations

| Validation | Implementation | Verdict |
|-----------|---------------|---------|
| Legal entity required | Generate button not shown until entity selected (template `@if`) | OK |
| Site required | `[disabled]="!selectedSiteCode \|\| generating()"` on generate button | OK |
| Environment optional | Spread operator only includes if non-null | OK |
| Prevent double-click | `[loading]="generating()"` disables button during request | OK |
| Revoke disabled during request | `[disabled]="revoking()"` | OK |

### Backend Validations

| Validation | Implementation | Verdict |
|-----------|---------------|---------|
| Site exists | `FindSiteBySiteCodeAsync` → "SITE_NOT_FOUND" | OK |
| Site belongs to legal entity | `site.LegalEntityId != request.LegalEntityId` → "SITE_ENTITY_MISMATCH" | OK |
| Active token limit (5) | `CountActiveBootstrapTokensForSiteAsync` → "ACTIVE_TOKEN_LIMIT_REACHED" | OK |
| TOCTOU race guard | Post-save re-check count, revokes excess token | OK |
| Revoke: token exists | `FindBootstrapTokenByIdAsync` → "TOKEN_NOT_FOUND" | OK |
| Revoke: not already revoked | Status check → "TOKEN_ALREADY_REVOKED" | OK |
| Revoke: not already used | Status check → "TOKEN_ALREADY_USED" | OK |
| Revoke: not expired | Status + ExpiresAt check → "TOKEN_EXPIRED" | OK |
| Revoke: concurrency | `TrySaveChangesAsync` → "CONCURRENCY_CONFLICT" | OK |

### Missing Validations

- **No `environment` value validation on backend.** The `Environment` field on `GenerateBootstrapTokenRequest` is `string?` with no validation. Any arbitrary string (e.g. `"<script>alert(1)</script>"`) will be stored in the database and serialized into audit event JSON. While the frontend restricts to a dropdown, the API is open.

---

## 5. API Calls Trace

### API Call 1: Load Legal Entities

| Aspect | Detail |
|--------|--------|
| **Trigger** | Component constructor |
| **Frontend** | `masterDataService.getLegalEntities()` |
| **HTTP** | `GET /api/v1/master-data/legal-entities` |
| **Backend** | MasterDataController (PortalUser policy) |
| **Response model** | `LegalEntity[]` |

### API Call 2: Load Sites

| Aspect | Detail |
|--------|--------|
| **Trigger** | `onLegalEntityChange()` when entity selected |
| **Frontend** | `siteService.getSites({ legalEntityId, pageSize: 500, isActive: true })` |
| **HTTP** | `GET /api/v1/sites?legalEntityId=...&pageSize=500&isActive=true` |
| **Backend** | SitesController (PortalUser policy, access-resolver scoped) |
| **Response model** | `PagedResult<Site>` |

### API Call 3: Generate Bootstrap Token

| Aspect | Detail |
|--------|--------|
| **Trigger** | "Generate Token" button click |
| **Frontend** | `bootstrapTokenService.generate(req)` |
| **HTTP** | `POST /api/v1/admin/bootstrap-tokens` |
| **Auth** | PortalAdminWrite policy + legal entity scoping |
| **Backend handler** | `GenerateBootstrapTokenHandler` via MediatR |
| **Request** | `{ siteCode, legalEntityId, environment? }` |
| **Response** | `{ tokenId, rawToken, expiresAt, siteCode }` (201 Created) |
| **Errors** | 404 (SITE_NOT_FOUND), 400 (SITE_ENTITY_MISMATCH, ACTIVE_TOKEN_LIMIT_REACHED) |

### API Call 4: Revoke Bootstrap Token

| Aspect | Detail |
|--------|--------|
| **Trigger** | "Revoke Token" button click |
| **Frontend** | `bootstrapTokenService.revoke(tokenId)` |
| **HTTP** | `DELETE /api/v1/admin/bootstrap-tokens/{tokenId}` |
| **Auth** | PortalAdminWrite policy + legal entity scoping |
| **Backend handler** | `RevokeBootstrapTokenHandler` via MediatR |
| **Response** | `{ tokenId, revokedAt }` (200 OK) |
| **Errors** | 404 (TOKEN_NOT_FOUND), 409 (TOKEN_ALREADY_REVOKED, TOKEN_ALREADY_USED, TOKEN_EXPIRED, CONCURRENCY_CONFLICT) |

---

## 6. Issues Found

### F07-01: XSS in `printQr()` — Unsanitized String Interpolation into HTML (Medium)

**File:** `bootstrap-token.component.ts` lines 578–594
**Description:** `printQr()` uses `win.document.write()` with string interpolation of `token.siteCode` and `token.tokenId` directly into raw HTML. If a site code were to contain HTML/script characters (e.g., via a compromised backend response or a malicious site code in the database), this would execute arbitrary JavaScript in the print window context.

```typescript
win.document.write(`
  ...
  <p>Site: <strong>${token.siteCode}</strong></p>
  ...
  <p>Token ID: ${token.tokenId} ...</p>
`);
```

While `siteCode` is typically a short alphanumeric code and `tokenId` is a GUID, there is no HTML-escaping of these values before injection into `document.write()`. This is a defense-in-depth issue.

**Recommendation:** HTML-escape `token.siteCode` and `token.tokenId` before interpolation, or use DOM APIs (`createElement`, `textContent`) instead of `document.write()`.

---

### F07-02: QR Code Contains Raw Token in Plaintext (Low)

**File:** `bootstrap-token.component.ts` lines 535–544
**Description:** The QR code payload includes `pt: token.rawToken` — the raw bootstrap token in plain text. Anyone who photographs, screenshots, or scans the QR code obtains the provisioning token. The QR also includes `cu` (the API base URL), giving an attacker everything needed to register a rogue device.

While this is by design (QR is the delivery mechanism), the risk profile should be documented:
- A printed QR left unattended is a credential leak.
- The QR remains valid for 72 hours (token lifetime).
- The download/print features make it easy to create persistent copies.

**Recommendation:** Consider adding a warning message near the QR code about physical security. Consider shorter token lifetimes for QR-delivered tokens.

---

### F07-03: No Error Display on Site Load Failure (Low)

**File:** `bootstrap-token.component.ts` lines 427–437
**Description:** When `siteService.getSites()` fails, the `catchError` block sets `loadingSites` to false and returns `EMPTY`, but does not set the `error()` signal. The user sees an empty site dropdown with no indication that loading failed.

```typescript
catchError(() => {
  this.loadingSites.set(false);
  return EMPTY;
}),
```

**Recommendation:** Set `this.error.set('Failed to load sites. Please try again.')` in the catch block.

---

### F07-04: No Backend Validation of `environment` Field (Low)

**File:** `GenerateBootstrapTokenRequest.cs` line 9, `GenerateBootstrapTokenHandler.cs` line 64
**Description:** The `Environment` property on the request DTO is `string?` with no validation or whitelist. The frontend constrains it to `['PRODUCTION', 'STAGING', 'DEVELOPMENT', 'LOCAL']` via dropdown, but the API accepts any string. This value is stored in the database and serialized into audit event JSON payloads.

**Recommendation:** Add a validation attribute or handler check to whitelist allowed environment values (or null).

---

### F07-05: `document.write()` in `printQr()` Blocked by Some CSP Policies (Low)

**File:** `bootstrap-token.component.ts` lines 578–594
**Description:** `window.open()` + `document.write()` is a legacy pattern that may be blocked by strict Content Security Policy headers (`script-src` without `'unsafe-inline'`). If the portal deploys CSP, the print functionality will break silently.

**Recommendation:** Use a hidden `<iframe>` or a dedicated print stylesheet instead.

---

### F07-06: Silent `clipboard.writeText()` Failure with No Fallback (Info)

**File:** `bootstrap-token.component.ts` lines 517–532
**Description:** If `navigator.clipboard.writeText()` throws (e.g., non-HTTPS in dev, or user denies permission), the catch block is empty. The comment says "fallback: user can still reveal and manually copy" which is reasonable, but no toast/message is shown to indicate the copy failed.

**Recommendation:** Show an informational message on clipboard failure so users know they need to manually copy.

---

## 7. Backend Endpoint Trace Summary

| Backend Ref | Endpoint | Policy | Handler | Findings |
|-------------|----------|--------|---------|----------|
| B-24 | POST /api/v1/admin/bootstrap-tokens | PortalAdminWrite | GenerateBootstrapTokenHandler | F07-04 (env validation) |
| B-25 | DELETE /api/v1/admin/bootstrap-tokens/{tokenId} | PortalAdminWrite | RevokeBootstrapTokenHandler | Clean |
| B-51 | GET /api/v1/master-data/legal-entities | PortalUser | (MasterDataController) | N/A (not audited here) |
| B-38 | GET /api/v1/sites | PortalUser | (SitesController) | N/A (not audited here) |

---

## 8. Positive Observations

1. **Security-conscious token handling (M-17):** Raw token stored in plain field (not signal), stripped from signal for DevTools, cleared from memory after clipboard copy. Well-implemented.
2. **TOCTOU race guard:** Backend re-checks active token count after save and revokes excess tokens — solid concurrency protection.
3. **Concurrency-safe revocation:** Uses `TrySaveChangesAsync` to gracefully handle concurrent revoke/register operations.
4. **Token hash storage:** Only SHA-256 hash stored in DB, raw token never persisted server-side.
5. **Audit trail:** Both generate and revoke operations create `AuditEvent` records.
6. **Frontend route guard:** `roleGuard(['SystemAdmin'])` correctly restricts access.
7. **Backend access scoping:** Legal entity access checked on both generate and revoke paths.
8. **Token masking UI:** Token displayed as first 6 + last 4 chars by default, revealable on demand.
