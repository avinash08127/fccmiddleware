# Security Scan: src/cloud

## Scope

This review covers the cloud code under `src/cloud`, with emphasis on:

- authentication and authorization
- tenant isolation and object-level access control
- secret handling and credential storage
- anonymous ingress endpoints and webhook validation
- raw payload, telemetry, and diagnostic data exposure
- health, diagnostics, and operational surfaces

This is a source review, not a dynamic penetration test. Findings below are based on concrete code paths in the current repository state.

## Severity Guide

- `High`: cross-tenant access, credential compromise, or a directly exploitable auth failure
- `Medium`: meaningful data exposure, service abuse, or security control gaps with realistic impact
- `Low`: information disclosure or protocol debt with more limited direct impact

## Executive Summary

The cloud codebase has several strong controls already in place: hashed refresh tokens and API keys, JWT lifetime validation, HSTS outside development, startup validation for some sensitive config, tenant query filters, and constant-time comparison for several shared-secret lookups.

The highest-risk issues are not in the crypto primitives already present, but in missing authorization checks on several portal-facing agent administration endpoints and in plaintext storage of multiple FCC/vendor secrets in the database. There are also notable hardening gaps on anonymous ingress routes, especially around request throttling and unbounded body reads.

## Confirmed Findings

### 1. Cross-tenant portal authorization bypass in `AgentController`

**Severity:** `High`

**Affected code**

- `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs`
  - `GenerateBootstrapToken(...)`
  - `RevokeBootstrapToken(...)`
  - `Decommission(...)`
  - `GetDiagnosticLogs(...)`
- `src/cloud/FccMiddleware.Application/Registration/GenerateBootstrapTokenHandler.cs`
- `src/cloud/FccMiddleware.Application/Registration/RevokeBootstrapTokenHandler.cs`
- `src/cloud/FccMiddleware.Application/Registration/DecommissionDeviceHandler.cs`
- `src/cloud/FccMiddleware.Application/DiagnosticLogs/GetDiagnosticLogsHandler.cs`

**Evidence**

- `AgentController` does not inject or use `PortalAccessResolver` at all.
- `GenerateBootstrapToken(...)` accepts caller-supplied `LegalEntityId` and `SiteCode`, but performs no access check before dispatching the command.
- `RevokeBootstrapToken(...)` revokes by token ID only, with no tenant-scope validation in the controller or handler.
- `Decommission(...)` decommissions by `deviceId` only, with no tenant-scope validation in the controller or handler.
- `GetDiagnosticLogs(...)` is only protected by `PortalUser`, then fetches logs by `deviceId` directly through `GetDiagnosticLogsHandler`, which also performs no tenant-scope enforcement.

**Impact**

A portal user with the right role but limited tenant scope can potentially:

- generate bootstrap tokens for a different legal entity
- revoke another tenant's bootstrap token if the token ID is known or guessed from logs/workflows
- decommission another tenant's device by device ID
- retrieve diagnostic logs for devices outside their allowed legal entities

This is a broken object-level authorization issue and should be treated as a priority fix.

**Recommended remediation**

- Inject `PortalAccessResolver` into `AgentController` and enforce `access.CanAccess(...)` on every portal-facing action.
- For `GenerateBootstrapToken(...)`, verify the caller can access `request.LegalEntityId` before issuing the command.
- For `RevokeBootstrapToken(...)`, load the token first and enforce access against `token.LegalEntityId`.
- For `Decommission(...)` and `GetDiagnosticLogs(...)`, resolve the target device's legal entity first and enforce access before proceeding.
- Add integration tests proving forbidden cross-tenant access for all four endpoints.

### 2. Multiple FCC and vendor secrets are stored in plaintext in the database

**Severity:** `High`

**Affected code**

- `src/cloud/FccMiddleware.Domain/Entities/FccConfig.cs`
- `src/cloud/FccMiddleware.Infrastructure/Persistence/Configurations/FccConfigConfiguration.cs`
- `src/cloud/FccMiddleware.Infrastructure/Repositories/SiteFccConfigProvider.cs`

**Evidence**

The `FccConfig` entity stores several secret-bearing fields directly:

- `SharedSecret`
- `FcAccessCode`
- `ClientSecret`
- `WebhookSecret`
- `AdvatecWebhookToken`

The EF configuration maps these directly to database columns:

- `shared_secret`
- `fc_access_code`
- `client_secret`
- `webhook_secret`
- `advatec_webhook_token`

`SiteFccConfigProvider` then reads and projects those values back into runtime config objects for normal operation.

**Impact**

Any database compromise, overly broad DB read access, backup exposure, or log/export mishandling exposes live upstream credentials and webhook secrets. This expands a DB read incident into compromise of external FCC/vendor integrations.

The code comments already establish the desired pattern for `CredentialRef`: use a secret reference rather than storing the credential itself. That pattern is not consistently applied to the other sensitive values.

**Recommended remediation**

- Move all secret values in `FccConfig` to a secret manager or equivalent encrypted secret store.
- Replace plaintext fields with references or secret IDs.
- Encrypt any unavoidable at-rest secrets with application-managed key material and strict key rotation.
- Plan a migration that rotates all currently stored webhook secrets and vendor client secrets after the storage model is fixed.

### 3. Anonymous ingress endpoints accept unbounded request bodies and have no application rate limiting

**Severity:** `Medium`

**Affected code**

- `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs`
- `src/cloud/FccMiddleware.Api/Auth/FccHmacAuthHandler.cs`
- `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs`
- `src/cloud/FccMiddleware.Api/Program.cs`

**Evidence**

Anonymous or secret-authenticated endpoints read full request bodies into memory:

- Radix XML ingress reads the full body with `ReadToEndAsync(...)`
- Petronite webhook reads the full body with `ReadToEndAsync(...)`
- Advatec webhook reads the full body with `ReadToEndAsync(...)`
- FCC HMAC auth enables buffering and reads the full request body before controller execution

At the same time, there is no application-level rate limiting or request body size policy configured in `Program.cs`.

The most exposed routes include:

- `/api/v1/ingest/radix`
- `/api/v1/ingest/petronite/webhook`
- `/api/v1/ingest/advatec/webhook`
- `/api/v1/agent/register`
- `/api/v1/agent/token/refresh`

**Impact**

An attacker can force the service to allocate memory and perform repeated secret checks or XML/JSON parsing on large or numerous requests, increasing the risk of:

- memory pressure and request amplification
- CPU exhaustion on signature checking and parsing
- brute-force attempts against bootstrap tokens, refresh tokens, or webhook secrets

**Recommended remediation**

- Add ASP.NET Core rate limiting for anonymous and secret-authenticated routes.
- Enforce request size limits per endpoint, especially for webhook and XML routes.
- Prefer streaming or bounded reads for request body verification where feasible.
- Add upstream protections as well: WAF, reverse-proxy rate limits, and body-size caps.

### 4. Advatec webhook secret is accepted in the URL query string

**Severity:** `Medium`

**Affected code**

- `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs`

**Evidence**

`IngestAdvatecWebhook(...)` accepts the secret from either:

- `X-Webhook-Token` header, or
- `?token=` query parameter

The code explicitly falls back to `Request.Query["token"]` when the header is absent.

**Impact**

Secrets in URLs are routinely exposed through:

- reverse proxy and CDN logs
- browser history and clipboard handling
- referrer leakage in downstream tooling
- observability systems that record request URLs

Even when the service itself does not log the full query string, other infrastructure commonly does.

**Recommended remediation**

- Remove query-string token support.
- Accept the secret only in a header.
- Rotate any deployed Advatec webhook tokens that may have been distributed in URLs.

### 5. Sensitive operational payloads are persisted and exposed broadly through portal surfaces

**Severity:** `Medium`

**Affected code**

- `src/cloud/FccMiddleware.Application/Ingestion/IngestTransactionHandler.cs`
- `src/cloud/FccMiddleware.Infrastructure/DeadLetter/DeadLetterService.cs`
- `src/cloud/FccMiddleware.Domain/Entities/DeadLetterItem.cs`
- `src/cloud/FccMiddleware.Application/Telemetry/SubmitTelemetryHandler.cs`
- `src/cloud/FccMiddleware.Infrastructure/Persistence/Configurations/AgentTelemetrySnapshotConfiguration.cs`
- `src/cloud/FccMiddleware.Api/Controllers/DlqController.cs`
- `src/cloud/FccMiddleware.Api/Controllers/AgentsController.cs`
- `src/cloud/FccMiddleware.Api/Controllers/AuditController.cs`
- `src/cloud/FccMiddleware.Api/Program.cs`

**Evidence**

- Raw ingestion payloads are stored in dead-letter records as `RawPayloadJson` on validation and adapter failures.
- Full telemetry payloads are serialized into `AgentTelemetrySnapshot.PayloadJson` as JSONB.
- Portal endpoints return these operational payloads to users under the broad `PortalUser` policy, which includes `SupportReadOnly`.

Examples:

- `DlqController.GetById(...)` returns `RawPayload`
- `AgentsController.GetTelemetry(...)` returns deserialized telemetry payloads
- `AuditController` returns full audit event payloads

**Impact**

These payloads can include sensitive infrastructure details, operational state, customer context, or vendor-specific transaction content. Even if tenant scoping is correct on most of these endpoints, the current access model makes sensitive operational data available to a broad read-only portal audience.

This is especially important because the telemetry snapshot stores `FccHost` and related operational data, and dead-letter payloads may contain unredacted raw vendor transaction content.

**Recommended remediation**

- Reassess whether `SupportReadOnly` should receive full payload visibility.
- Split "summary" access from "sensitive payload" access into separate roles/policies.
- Redact or minimize stored raw payload fields when not strictly needed for replay.
- For DLQ detail views, consider returning only metadata by default and gating raw payload access behind a stronger policy.

## Hardening Observations

These are real security concerns, but the code suggests they may be compatibility or operational tradeoffs rather than immediate standalone exploits.

### 6. Unauthenticated health endpoints disclose dependency state and version data

**Severity:** `Low`

**Affected code**

- `src/cloud/FccMiddleware.Api/Program.cs`
- `src/cloud/FccMiddleware.Api/Infrastructure/HealthResponseWriter.cs`

**Observation**

`/health` and `/health/ready` are publicly mapped without authentication. The JSON response includes:

- service version
- dependency names
- dependency status
- dependency messages

This is useful for operations, but it also provides reconnaissance data to unauthenticated callers.

**Recommendation**

- Keep `/health` minimal for public liveness checks.
- Restrict `/health/ready` to internal networks, a management plane, or authenticated infrastructure.
- Remove dependency messages from unauthenticated responses.

### 7. Radix signature validation depends on SHA-1 and uses non-constant-time comparison

**Severity:** `Low`

**Affected code**

- `src/cloud/FccMiddleware.Adapter.Radix/Internal/RadixSignatureHelper.cs`
- `src/cloud/FccMiddleware.Adapter.Radix/RadixCloudAdapter.cs`

**Observation**

The Radix path validates signatures as `SHA1(xmlContent + sharedSecret)` and compares them with ordinary `string.Equals(...)`.

This appears to be inherited from the vendor protocol, so it may not be fully replaceable without breaking compatibility. Even so, SHA-1 is deprecated for new security designs and the string comparison is not constant-time.

**Recommendation**

- Treat this as protocol debt and isolate it to the minimum possible surface.
- If the vendor supports a stronger scheme, migrate.
- If not, add compensating controls around the endpoint: IP allowlists, rate limiting, strict body-size limits, and tenant-specific secret rotation.

### 8. Local filesystem fallback for raw payload and archive storage can weaken at-rest protections

**Severity:** `Low`

**Affected code**

- `src/cloud/FccMiddleware.Infrastructure/Storage/S3RawPayloadArchiver.cs`
- `src/cloud/FccMiddleware.Infrastructure/Storage/ArchiveObjectStore.cs`

**Observation**

When S3 is not configured, the code falls back to local temp paths:

- raw payloads under `%TEMP%/fccmiddleware-raw-payloads`
- archive artifacts under `%TEMP%/fccmiddleware-archive`

S3 uploads use KMS or AES256, but local fallback writes plaintext files to disk.

This may be acceptable for local development, but it is risky if fallback remains enabled in shared non-development environments.

**Recommendation**

- Disable local fallback outside development/test.
- Fail closed in production when durable encrypted object storage is unavailable.
- If local fallback must exist, use a dedicated directory with explicit ACLs and an encrypted volume.

## Positive Controls Observed

The following controls are already implemented and reduce risk:

- Device JWTs and portal JWTs validate issuer, audience, signature, and lifetime.
- Refresh tokens and bootstrap tokens are stored as SHA-256 hashes, not plaintext.
- Odoo and Databricks API keys are stored as SHA-256 hashes.
- Device refresh token rotation includes reuse detection and token-family revocation.
- Several webhook secret comparisons use `CryptographicOperations.FixedTimeEquals(...)`.
- HSTS is enabled outside development.
- Startup validation blocks some high-risk secrets from being sourced from JSON config in non-development environments.
- Tenant query filters are broadly present in EF Core and many portal controllers do explicit tenant checks.

## Recommended Remediation Order

1. Fix `AgentController` tenant authorization gaps and add regression tests.
2. Remove plaintext secret storage from `FccConfig` and rotate affected secrets.
3. Add rate limiting and body-size limits to anonymous and secret-authenticated endpoints.
4. Remove Advatec query-string token support and rotate any deployed tokens.
5. Tighten portal access to raw payloads, diagnostic logs, and full telemetry payloads.
6. Restrict readiness/diagnostic surfaces and reduce unauthenticated health detail.
7. Treat Radix SHA-1 validation as compatibility debt and add compensating controls.

## Suggested Follow-up Testing

- Integration tests for cross-tenant denial on all `AgentController` portal actions.
- Negative tests for oversized bodies on webhook and ingest routes.
- Rate-limit tests on bootstrap token, refresh token, and webhook endpoints.
- Secret rotation rehearsal for webhook and FCC credentials currently stored in `fcc_configs`.
- Review of portal role design, especially whether `SupportReadOnly` should receive raw payload and log access.