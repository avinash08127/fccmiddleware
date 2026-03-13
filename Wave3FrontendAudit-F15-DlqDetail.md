# Wave 3 — F-15 DlqDetailComponent Audit

**Route:** `/dlq/items/:id`
**Component:** `DlqDetailComponent` (`src/portal/src/app/features/dlq/dlq-detail.component.ts`)
**Service:** `DlqService` (`src/portal/src/app/core/services/dlq.service.ts`)
**Backend:** `DlqController` (`src/cloud/FccMiddleware.Api/Controllers/DlqController.cs`)
**Route guard:** `roleGuard(['SystemAdmin', 'OperationsManager'])`
**Date:** 2026-03-13

---

## Summary

The DLQ detail page displays comprehensive error information for a single dead-letter item including error details, original payload, retry history, and action buttons (retry/discard) gated by role. It traces to `DlqController.GetById`, `Retry`, and `Discard` endpoints. The component is well-structured with proper signal-based state management and subscription cleanup. However, there is a backend/frontend type mismatch that silently loses retry error messages, enum drift issues inherited from the shared model, and ambiguous UI states for restricted payloads.

---

## Issues Found

### F15-01 · Retry error message lost — `RetryResultDto.Error` type mismatch [MEDIUM]

**Frontend:** `src/portal/src/app/features/dlq/dlq-detail.component.ts:601`
**Frontend model:** `src/portal/src/app/core/models/dlq.model.ts:81-85`
**Backend:** `src/cloud/FccMiddleware.Contracts/Portal/PortalDlqContracts.cs:47-51`
**Backend controller:** `src/cloud/FccMiddleware.Api/Controllers/DlqController.cs:205-210`

Backend `RetryResultDto.Error` is `object?` and is set to `result.ErrorMessage` (a plain string). Frontend `RetryResult.error` is typed as `ErrorResponse | null` (an object with a `message` property). The component reads `result.error?.message` — accessing `.message` on a JavaScript string returns `undefined`, so the `??` fallback always produces the generic `'Retry failed.'`.

**Impact:** The actual backend error reason (e.g., "Replay service unavailable", "Payload corrupted") is silently discarded. Users always see the unhelpful generic message instead of the specific error. Debugging failed retries requires checking backend logs.

---

### F15-02 · `REPLAY_QUEUED` items show retry button — may cause duplicate replay [MEDIUM]

**Frontend:** `src/portal/src/app/features/dlq/dlq-detail.component.ts:250-253`
**Backend:** `src/cloud/FccMiddleware.Api/Controllers/DlqController.cs:198`

The action panel is shown when `status !== DISCARDED && status !== RESOLVED`. Since `REPLAY_QUEUED` and `REPLAY_FAILED` are not in the frontend enum (see F15-03), items in those states pass this guard and show the Retry button. The backend `Retry` endpoint also only blocks `RESOLVED` and `DISCARDED` — it does not block `REPLAY_QUEUED`.

**Impact:** A user could click Retry on an item that is already queued for replay, potentially triggering a duplicate replay. For `REPLAY_FAILED` items, retrying is the correct action, but for `REPLAY_QUEUED`, it's a race condition.

---

### F15-03 · Frontend `DeadLetterStatus` enum missing `REPLAY_QUEUED` and `REPLAY_FAILED` [MEDIUM]

**Frontend:** `src/portal/src/app/core/models/dlq.model.ts:10-15`
**Backend:** `src/cloud/FccMiddleware.Domain/Enums/DeadLetterStatus.cs`

Backend defines 6 statuses: `PENDING`, `REPLAY_QUEUED`, `RETRYING`, `RESOLVED`, `REPLAY_FAILED`, `DISCARDED`. Frontend only has 4: `PENDING`, `RETRYING`, `RESOLVED`, `DISCARDED`.

**Impact on detail page:** The `statusSeverity()` function falls through to the `default: return 'contrast'` case, rendering an unstyled badge. More critically, the terminal-state guard (F15-02) treats these as actionable states. Also already noted in F14-02.

---

### F15-04 · `rawPayload` null-state is ambiguous — permission denial indistinguishable from missing payload [MEDIUM]

**Frontend:** `src/portal/src/app/features/dlq/dlq-detail.component.ts:198-208`
**Backend:** `src/cloud/FccMiddleware.Api/Controllers/DlqController.cs:168-169`
**Backend mapper:** `src/cloud/FccMiddleware.Api/Controllers/DlqController.cs:456-458`

Backend returns `null` for `rawPayload` in two different scenarios: (a) no payload was stored for the item (`RawPayloadJson` is null/empty), or (b) the user lacks `HasSensitiveDataAccess` permission. The frontend shows the same "Original payload is not available for this item" message for both cases.

**Impact:** Users with `SupportReadOnly` role (not in `SensitiveDataRoles`) see the generic "not available" message with no indication that the payload exists but is permission-restricted. They may file support tickets asking why payloads are missing, not realizing it's an access control decision.

---

### F15-05 · Missing `NORMALIZATION_FAILURE` in frontend `DeadLetterReason` enum [LOW]

**Frontend:** `src/portal/src/app/core/models/dlq.model.ts:17-23`
**Backend:** `src/cloud/FccMiddleware.Domain/Enums/DeadLetterReason.cs`

Backend has 6 reasons; frontend has 5 — missing `NORMALIZATION_FAILURE`. The detail page displays the `failureReason` field as plain text, so it renders correctly as a raw string. No functional break, but inconsistent with the typed model. Also noted in F14-01.

---

### F15-06 · No confirmation dialog before retry action [LOW]

**File:** `src/portal/src/app/features/dlq/dlq-detail.component.ts:586-608`

The discard flow has a confirmation dialog with mandatory reason text. Retry executes immediately on button click with no confirmation. DLQ retry re-queues the item for processing which has real side effects (may re-submit a transaction to a vendor adapter, increment retry count, etc.).

**Impact:** An accidental click on "Retry" immediately dispatches the replay. Unlike discard, there's no way to cancel.

---

### F15-07 · Discard reason minimum length inconsistent between detail and list pages [LOW]

**Detail page:** `src/portal/src/app/features/dlq/dlq-detail.component.ts:316` — requires only non-empty (`!discardReason.trim()`)
**List page (batch):** enforces `batchDiscardReason.trim().length < 10`
**Backend:** `src/cloud/FccMiddleware.Contracts/Portal/PortalDlqContracts.cs:41-44` — no validation at all

Single-item discard accepts a 1-character reason. Batch discard requires 10+ characters. Backend accepts any non-null string including empty via direct API call.

---

### F15-08 · `copyPayload()` fails silently on non-HTTPS contexts [LOW]

**File:** `src/portal/src/app/features/dlq/dlq-detail.component.ts:640-642`

`navigator.clipboard.writeText()` requires a secure context (HTTPS). The `.catch(() => {})` swallows all errors with no user feedback. In development or staging environments running on HTTP, the copy button does nothing.

---

### F15-09 · Action feedback auto-dismisses error messages after 5 seconds [LOW]

**File:** `src/portal/src/app/features/dlq/dlq-detail.component.ts:665-668`

`setAction()` applies a 5-second `setTimeout` for both success and error severities. Error messages disappear before users may notice them, especially if they clicked Retry/Discard and briefly looked away. Success messages auto-dismissing is reasonable, but error messages should persist or have a longer timeout.

---

## API Trace

| Frontend Action | Service Method | HTTP | Backend Endpoint | Auth |
|----------------|---------------|------|-----------------|------|
| Page load | `dlqService.getDeadLetterById(id)` | GET `/api/v1/dlq/{id}` | `DlqController.GetById` | PortalUser |
| Retry button | `dlqService.retry(id)` | POST `/api/v1/dlq/{id}/retry` | `DlqController.Retry` | PortalAdminWrite |
| Confirm discard | `dlqService.discard(id, reason)` | POST `/api/v1/dlq/{id}/discard` | `DlqController.Discard` | PortalAdminWrite |
| Copy payload | `navigator.clipboard.writeText()` | — (browser) | — | — |
| Back button | `router.navigate(['/dlq/list'])` | — (route) | — | — |

---

## What Works Well

- Signal-based state management with `loading`, `item`, `retryLoading`, `discardLoading` prevents stale-state rendering
- `takeUntilDestroyed(this.destroyRef)` on all subscriptions prevents memory leaks
- Discard confirmation dialog with mandatory reason text prevents accidental data loss
- Retry/discard buttons are mutually disabled during loading (lines 263, 274) preventing double-submit
- Role gating via `*appRoleVisible="['SystemAdmin', 'OperationsManager']"` matches backend `PortalAdminWrite` policy
- Terminal states (`DISCARDED`, `RESOLVED`) correctly hide the actions panel
- `formattedPayload` computed signal handles both string and object payloads gracefully
- Backend `GetById` properly validates tenant access with `CanAccess(item.LegalEntityId)` after fetching
- Backend `Discard` creates an audit trail via `AuditEvent` with user ID, reason, and correlation
- Route-level `roleGuard` prevents unauthorized navigation to the page
- Retry count highlighting (`>= 3`) draws attention to frequently failing items
