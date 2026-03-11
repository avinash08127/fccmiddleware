# Forecourt Middleware Platform
## WIP High Level Design - Edge Agent

Status: Working Draft  
Authoring Context: Derived from `Requirements.md`, `HighLevelRequirements.md`, `FlowDiagrams.md`, and the additional sizing/authentication constraints provided on 2026-03-10.  
Important Constraint: The requirement set explicitly resolves the Edge Agent to a native Android implementation on the HHT. This HLD keeps that decision because it is the most practical fit for the Urovo i9100 / Android 12 runtime and LAN-first requirements.

## 1. Overview

### Purpose of This Subsystem

The Edge Agent is the station-side runtime that preserves forecourt continuity when cloud connectivity is weak, absent, or unstable. It is the bridge between Odoo POS on the HHT, the local FCC on the station LAN, and the cloud backend.

### Business Context

At many stations, internet is not reliable enough to make cloud-only fuel authorization or transaction capture safe. The station LAN to the FCC is materially more reliable than the internet path. The Edge Agent exists to exploit that difference:

- pre-auth must work even during internet outages
- missed transactions must still be captured locally
- Odoo POS must continue to create orders from FCC-originated facts, not from manual reconstruction

### Major Responsibilities

- Communicate with the FCC over LAN using vendor adapters
- Authorize pre-auth requests locally
- Poll or relay FCC transactions depending on site ingestion mode
- Buffer transactions and pre-auth records durably on-device
- Replay buffered data to the cloud in order when connectivity returns
- Expose local APIs to Odoo POS and, in multi-HHT mode, to other HHTs
- Report health, telemetry, and compatibility status to the cloud
- Enforce device identity and secure local/cloud communications

### Boundaries and Exclusions

Included:

- device runtime, local buffering, LAN communication, local API, provisioning, telemetry, replay logic, and FCC adapter execution

Excluded:

- central reconciliation logic
- enterprise user authentication
- Odoo internal offline behavior beyond the documented integration points
- automatic multi-HHT failover in MVP
- detailed Android UI screen designs

### Primary Requirement Alignment

- REQ-6 to REQ-9: pre-auth, normal-order capture support, Odoo offline poll model
- REQ-12 to REQ-16: ingestion modes, dedup support, audit/retry behavior, edge buffering and diagnostics

## 2. Design Goals

### Scalability

- Support busy stations at up to 1,000 transactions/day with a minimum 30-day local retention window.
- Operate on constrained rugged devices without degrading Odoo POS usability.

### Configurability

- Allow per-site provisioning of FCC vendor, host/port, polling interval, ingestion mode, cloud URL, and LAN exposure mode.
- Avoid per-site app forks or manual APK customization.

### Resilience

- Survive app restarts, battery issues, intermittent network changes, and partial sync failures without losing buffered transactions.
- Treat cloud unavailability as normal, not exceptional.

### Security

- Protect device secrets and FCC credentials on shared field devices.
- Distinguish device-level machine access from employee login context.

### Maintainability

- Keep runtime responsibilities focused and explicit.
- Isolate vendor adapters from buffer, sync, and API layers so new FCCs can be added without destabilizing core flows.

### Multi-Country Readiness

- Support per-country timezone, currency, fiscalization requirements, and site policies via downloaded configuration.

### Low Operational Friction

- Provision by QR code where possible.
- Expose enough diagnostics that field support can solve most issues without adb-level debugging.

## 3. Functional Scope

### Key Features

- FCC LAN connectivity and heartbeat monitoring
- Pre-auth local authorization relay to FCC
- Catch-up polling in `CLOUD_DIRECT`
- Primary ingestion in `RELAY` and `BUFFER_ALWAYS`
- Durable local SQLite buffer for transactions and pre-auth records
- Ordered replay to cloud
- Local API for Odoo POS transaction access and pre-auth
- Multi-HHT support with one manually designated primary agent
- Telemetry and version-compatibility reporting
- Local diagnostics and manual pull

### Major Use Cases

- Authorize a fiscalized pre-auth order while internet is down
- Capture normal orders locally when FCC cannot reach cloud
- Serve buffered transactions to Odoo POS during outage
- Upload backlog once internet returns and suppress already-synced transactions using cloud status sync
- Support multiple attendant HHTs by exposing LAN-accessible agent APIs from the primary device

### Supported Operational Scenarios

- Fully online with FCC push direct to cloud and Edge as safety-net poller
- Internet down but FCC LAN up
- FCC unreachable while internet remains up
- Fully offline manual mode
- Sites where FCC can only reach the HHT and not the cloud

## 4. Architecture Overview

### Recommended Architecture Style

Recommended style: native Android edge runtime with explicit local service boundaries and adapter isolation.

Rationale:

- Android 12 HHT constraints, local LAN requirements, and requirement resolution make native runtime the lowest-risk option.
- The edge runtime is an operational appliance, not a generic application platform.
- Reliability and controlled persistence matter more here than cross-platform code reuse.

### Logical Component Model

1. Device Runtime Shell
   Lifecycle management, boot sequencing, foreground/background service coordination, and health supervision.
2. Provisioning and Configuration Module
   QR/manual setup, device registration, config refresh, and compatibility checks.
3. FCC Adapter Host
   Vendor-specific protocol clients for DOMS first, then later vendors.
4. Local Transaction Buffer
   SQLite persistence for transactions, pre-auth records, sync state, and diagnostics metadata.
5. Sync Engine
   Cloud upload, replay ordering, backoff, acknowledgement handling, and status sync.
6. Local API Server
   Exposes `localhost` endpoints for same-device Odoo POS and optional LAN endpoints for non-primary HHTs.
7. Connectivity Monitor
   Internet health checks, FCC heartbeat checks, and mode transition handling.
8. Telemetry and Diagnostics Module
   Metrics, logs, local diagnostics view, and cloud health reporting.

### Key Interactions

- Odoo POS calls local API for pre-auth and offline transaction retrieval.
- FCC adapter host talks to controller over station LAN.
- Sync engine uploads records to cloud and fetches config/status updates.
- Connectivity monitor influences API responses, sync scheduling, and operational status.

### Runtime Flow View

#### Pre-Auth

1. Odoo POS sends pre-auth to local agent API.
2. Agent validates request against downloaded site config.
3. FCC adapter sends authorize-by-amount command to FCC over LAN.
4. Agent stores pre-auth locally and returns authorization result to Odoo POS.
5. Sync engine forwards pre-auth record to cloud when internet is available.

#### Normal Order with Internet Down

1. Agent polls FCC or receives relayed transaction per ingestion mode.
2. Transaction is written durably to SQLite as `PENDING`.
3. Odoo POS polls local API and creates order from buffered transaction.
4. When internet returns, sync engine uploads backlog in order.
5. Agent polls cloud for `SYNCED_TO_ODOO` status and stops serving those records locally.

## 5. Project Structure Recommendation

### Repository / Module Structure

Recommended approach: one dedicated Android repository with clear package boundaries and adapter modules.

```text
/app
  /src/main/java/.../runtime
  /src/main/java/.../provisioning
  /src/main/java/.../config
  /src/main/java/.../api
  /src/main/java/.../sync
  /src/main/java/.../connectivity
  /src/main/java/.../storage
  /src/main/java/.../telemetry
  /src/main/java/.../adapters
    /doms
    /radix
    /advatec
    /petronite
/shared-contracts
/tests
  /unit
  /instrumentation
  /protocol-sim
/docs
  /provisioning
  /adr
```

### Internal Layering

- `runtime`: Android services, app lifecycle, scheduling
- `core`: domain models and state machines for transaction, sync, and health status
- `storage`: SQLite/Room or direct SQLite access, retention, integrity checks
- `api`: local HTTP server handlers and request validation
- `adapters`: FCC vendor implementations behind a common interface
- `sync`: cloud client, replay orchestration, backoff, status sync

### Shared Contracts

Do not force shared runtime code with the .NET backend. Share only:

- canonical payload schemas
- API contracts
- event names and status enums
- validation rules that can be expressed declaratively

That keeps cloud and device runtimes independently evolvable while preserving protocol consistency.

## 6. Integration View

### Upstream and Downstream Systems

| System | Direction | Pattern | Notes |
|---|---|---|---|
| Odoo POS on same HHT | Upstream/downstream | Local REST over localhost | Primary offline integration path |
| Non-primary HHTs | Upstream/downstream | LAN REST with API key | Only for multi-HHT sites |
| FCC | Downstream and upstream | Vendor-specific LAN protocol | REST/TCP/SOAP depending on vendor |
| Cloud Backend | Upstream/downstream | HTTPS API | Upload, config, status sync, telemetry |
| Sure MDM | Downstream management | MDM deployment channel | App rollout and updates |

### Sync Patterns

- `CLOUD_DIRECT`: Edge polls FCC as catch-up and uploads missed transactions
- `RELAY`: FCC talks to Edge first; Edge relays to cloud in near real time when online
- `BUFFER_ALWAYS`: Edge stores first and uploads on schedule

### Retry and Ordering

- Transactions replay in chronological order within a site buffer
- Batched upload is allowed, but ordering must be preserved inside a batch sequence
- Exponential backoff with jitter for retryable failures
- Hard validation failures are quarantined locally and surfaced in diagnostics

### Idempotency Considerations

- Cloud dedup is authoritative, but the Edge Agent should still avoid duplicate uploads of already confirmed records
- Local records track cloud-accepted, cloud-rejected, and `SYNCED_TO_ODOO` states separately
- Manual pull results and scheduled pull results must converge into one local record model

### Online/Offline Handling

- Internet state and FCC LAN state are evaluated independently
- Internet loss does not disable pre-auth or local transaction serving
- FCC LAN loss triggers alerting and possibly manual operating fallback, but the agent continues cloud sync for buffered items if internet remains up

## 7. Security Architecture

### Authentication and Device Identity

- Each primary Edge Agent receives a unique device identity tied to site and device serial metadata.
- Preferred runtime credential model: device keypair in Android Keystore with certificate- or signed-token-based authentication to cloud.
- Non-primary HHT access to the primary agent uses a site-scoped API key plus short-lived session token if later needed.

### Authorization

- Device permissions are site-scoped only.
- Non-primary HHT calls are restricted to allowed local operations such as transaction fetch, pump status, and pre-auth submission.
- Administrative functions such as reprovisioning or viewing advanced diagnostics require supervisor-only access in the local UI.

### Secrets Handling

- FCC credentials stored using Android Keystore-backed encryption
- Cloud bootstrap token is single-use and rotated away after registration
- No storage of employee Entra tokens beyond transient Odoo/portal contexts

### Local API Security

- Bind to `localhost` by default
- Enable LAN binding only when site configuration explicitly marks the device as primary multi-HHT agent
- Require API key on all non-local requests
- Prefer station-WiFi-only exposure with explicit IP allow rules if feasible

### Data Protection

- Encrypt sensitive config values at rest on-device
- Use TLS for all cloud communication
- Keep local logs free of secrets and customer TIN unless strictly required for diagnostics

### Audit and Tamper Awareness

- Record provisioning changes, manual retries, mode transitions, config updates, and supervisor actions
- Surface clock skew, repeated auth failures, or SQLite integrity failures as tamper or reliability signals

## 8. Deployment Architecture

### Recommended Deployment Model

- Deployed as a managed APK on Urovo i9100 Android 12 HHTs
- One primary agent per site for MVP
- Optional LAN API exposure for other HHTs on the same station network

### Provisioning Strategy

1. Device receives APK through Sure MDM or controlled sideload
2. Supervisor scans QR code or enters bootstrap details manually
3. Agent registers with cloud and receives runtime config, device identity, and compatibility policy
4. Agent performs FCC connectivity test and stores baseline diagnostics

### Environment Strategy

- Device points to environment-specific cloud base URL
- Separate bootstrap credentials per environment
- UAT should include realistic multi-HHT and outage simulation, not just happy-path connectivity

### Resilience and Recovery

- SQLite WAL mode
- startup integrity check
- backup of corrupted DB before reset
- replay-safe resynchronization after app restart or device reboot

### Scaling Approach

Edge scaling is horizontal by station count, not by vertical device power. The design must minimize CPU, battery, and storage overhead on each HHT.

### Observability

- device metrics: battery, storage, app version, sync lag, buffer depth
- integration metrics: FCC heartbeat age, cloud reachability, failed upload counts
- local diagnostics screen with recent logs and manual pull

## 9. Key Design Decisions

### Decision 1: Native Android Runtime

Reason:

- requirement set already resolved this, and it is the lowest-risk fit for Android HHT operation, local networking, and device security APIs

Trade-off:

- no direct runtime code sharing with the .NET backend

### Decision 2: One Primary Agent per Site for MVP

Reason:

- avoids split-brain FCC communication and simplifies offline consistency

Trade-off:

- manual promotion is required if the primary HHT fails

### Decision 3: SQLite Durable Store-and-Forward

Reason:

- simple, mature, and adequate for 30K+ retained records per device

Trade-off:

- careful schema evolution and corruption recovery are required

### Decision 4: Local API as Odoo Offline Integration Contract

Reason:

- keeps Odoo POS reading FCC-originated transaction facts during outages

Trade-off:

- local API compatibility becomes a release-critical contract

### Assumptions

- Station LAN remains available independently of internet outages
- Primary agent device remains powered and connected during most station operation
- Odoo POS can call either localhost or LAN primary-agent endpoints as defined

### Known Risks

- Some FCC protocols may behave poorly on long-running Android network sessions
- HHT battery/storage issues can degrade sync and local API behavior
- Multi-HHT LAN discovery and support workflow may become operationally messy if not tightly specified

### Areas Needing Validation / PoC

- Long-running FCC session stability on Urovo i9100 devices
- Background execution behavior under Android power-management policies
- Realistic replay speed after 2 to 7 days of outage
- LAN API behavior with multiple HHTs on typical station WiFi networks

## 10. Non-Functional Requirements Mapping

| NFR Area | HLD Response |
|---|---|
| Performance | Lightweight native runtime, local persistence, bounded polling, batch replay, careful battery/network usage |
| Availability | LAN-first operation, durable buffer, automatic reconnect, manual pull support |
| Recoverability | WAL mode, integrity checks, no delete before sync confirmation, cloud dedup on replay |
| Supportability | Diagnostics screen, telemetry upload, version check, supervisor-visible health indicators |
| Operability | QR provisioning, MDM rollout, explicit primary-agent model, simple local APIs |
| Extensibility | Adapter isolation, config-driven site behavior, shared contracts without forcing shared runtime code |

## 11. Recommended Technology Direction

### Runtime

- Native Kotlin on Android 12
- Java interoperability allowed where vendor SDKs require it
- Android foreground service or equivalent supervised runtime for reliability

### Local API

- Lightweight embedded HTTP server appropriate for Android
- JSON contracts aligned with backend schemas

### Persistence

- SQLite with WAL mode
- Room if the team values schema tooling and testability, or direct SQLite if tighter control is needed for performance and corruption handling

### Networking and Security

- OkHttp/Retrofit-style HTTP stack or equivalent
- Android Keystore for key material
- TLS certificate pinning for cloud endpoints where operationally manageable

### Design Patterns

- store-and-forward
- explicit state machine for transaction sync lifecycle
- adapter pattern for FCC vendors
- supervisor pattern for connectivity and background tasks

### Note on ".NET for Edge"

The supplied requirement set resolved the edge implementation to native Kotlin/Java. For this platform, that is the recommended direction. Standardizing edge on .NET would introduce additional delivery and runtime risk without clear business gain at this stage.

## 12. Open Questions / Pending Decisions

1. What exact local-network discovery/configuration method will non-primary HHTs use to find the primary agent IP in offline mode?
2. Should the primary agent expose LAN API only on private WiFi, or is there any scenario where hotspot networking changes that assumption?
3. Which FCC vendors require persistent sockets versus stateless polling, and how does that interact with Android background limits?
4. How many non-primary HHTs per site should be treated as the realistic upper bound for support and performance testing?
5. Is supervisor authentication on the local diagnostics screen delegated to Odoo user context, or handled with a separate local PIN/policy?
6. What minimum offline retention beyond 30 days is needed for rare prolonged outages or operational negligence cases?
