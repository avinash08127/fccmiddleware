# DeviceRegistration Contract

## 1. Output Location
- Target file path: `docs/specs/data-models/tier-1-1-device-registration-spec.md`
- Optional companion files: `schemas/canonical/device-registration.schema.json`
- Why this location matches `docs/STRUCTURE.md`: registration request and response bodies are shared contracts, so the human-readable model goes in `/docs/specs/data-models` and the companion schema goes in `/schemas/canonical`.

## 2. Scope
- TODO item addressed: `Define DeviceRegistration — registration request/response model`
- In scope: QR payload, registration request, registration response, and token-related fields needed by the model
- Out of scope: full authentication design, API status codes, decommission workflow, portal UX

## 3. Source Traceability
- Requirements referenced: `REQ-15.10`, `REQ-15.11`, `REQ-15.13`
- HLD sections referenced: `WIP-HLD-Cloud-Backend.md` sections `6.2`, `7.2`, `7.4`, `7.7`; `WIP-HLD-Edge-Agent.md` sections `7.1`, `7.2`, `7.5`, `8.2`
- Assumptions from TODO ordering/dependencies: `SiteConfig` is defined separately and is returned by reference here; API envelope details are covered in Tier `1.3`

## 4. Key Decisions

| Decision | Why | Impact |
|---|---|---|
| Bootstrap provisioning uses a single-use opaque token. | The QR code should not carry a long-lived secret. | Registration request includes `provisioningToken`; cloud stores and validates it server-side. |
| Registration response issues both `deviceId` and `deviceToken`. | The device needs a stable identity plus an auth token for later calls. | All later edge-to-cloud calls bind to the returned identity. |
| QR payload excludes FCC credentials. | FCC secrets belong in cloud-managed site configuration, not a scannable QR code. | QR data stays minimal; full config is returned in `siteConfig`. |
| Device metadata is inventory data, not security identity. | Ops needs to see physical device details without using them as the trust anchor. | Request includes serial/model/version fields for support and audit only. |

## 5. Detailed Specification

### QR payload

| Field | Type | Required | Produced By | Description |
|---|---|---|---|---|
| `v` | `int` | Yes | Portal | QR payload schema version. |
| `sc` | `string` | Yes | Portal | Site code used for registration. |
| `cu` | `string` | Yes | Portal | Cloud base URL the agent should call. |
| `pt` | `string` | Yes | Portal | Single-use provisioning token. |

### Registration request

| Field | Type | Required | Produced By | Description |
|---|---|---|---|---|
| `provisioningToken` | `string` | Yes | Edge Agent | Single-use bootstrap token from QR or manual entry. |
| `siteCode` | `string` | Yes | Edge Agent | Site code being claimed during registration. |
| `deviceSerialNumber` | `string` | Yes | Edge Agent | Stable hardware or Android identifier for inventory tracking. |
| `deviceModel` | `string` | Yes | Edge Agent | Physical device model reported by Android. |
| `osVersion` | `string` | Yes | Edge Agent | Android OS version string. |
| `agentVersion` | `string` | Yes | Edge Agent | Installed APK version. |
| `replacePreviousAgent` | `boolean` | No | Edge Agent | Explicit flag to replace an existing active site agent. |

### Registration response

| Field | Type | Required | Produced By | Description |
|---|---|---|---|---|
| `deviceId` | `uuid` | Yes | Cloud | Stable middleware identifier for the registered device. |
| `deviceToken` | `string` | Yes | Cloud | Signed bearer token used for subsequent cloud calls. |
| `tokenExpiresAt` | `datetime` | Yes | Cloud | UTC expiry timestamp for `deviceToken`. |
| `siteCode` | `string` | Yes | Cloud | Confirmed site code bound to the registration. |
| `legalEntityId` | `uuid` | Yes | Cloud | Legal entity bound to the site. |
| `siteConfig` | `SiteConfig` | Yes | Cloud | Full effective site configuration returned after registration. |
| `registeredAt` | `datetime` | Yes | Cloud | UTC timestamp when the registration completed. |

## 6. Validation and Edge Cases
- `provisioningToken` must be single-use and time-limited.
- `siteCode` in the request must match the provisioning token scope.
- Re-registering the same device for the same site should invalidate the prior active token.
- Replacing a different active device at the same site requires `replacePreviousAgent = true`.
- `deviceToken` expiry must be later than `registeredAt`.

## 7. Cross-Component Impact
- Cloud Backend: validates bootstrap token, creates the device record, returns the auth token and config.
- Edge Agent: submits the request and stores `deviceId`, `deviceToken`, and `siteConfig`.
- Angular Portal: generates the QR payload and controls provisioning-token issuance.

## 8. Dependencies
- Prerequisites: site config contract, security implementation decisions for token signing, site master data
- Downstream TODOs affected: registration API spec, security implementation plan, agent registration schema design
- Recommended next implementation step: detail the registration API contract and security flow against this model

## 9. Open Questions
None.

## 10. Acceptance Checklist
- [ ] QR payload, request body, and response body are each defined with field types and meanings.
- [ ] Bootstrap token and runtime token are clearly separated.
- [ ] Site replacement behavior is explicit.
- [ ] `SiteConfig` is referenced without duplicating its schema.
- [ ] Companion schema aligns with this contract.

## 11. Output Files to Create
- `docs/specs/data-models/tier-1-1-device-registration-spec.md`
- `schemas/canonical/device-registration.schema.json`

## 12. Recommended Next TODO
FCC Adapter Interface Contracts.
