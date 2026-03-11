
You are working on the Forecourt Middleware Platform design pack.

Context:
- Requirements are in `Requirements.md`
- High-level designs are in:
  - `WIP-HLD-Cloud-Backend.md`
  - `WIP-HLD-Edge-Agent.md`
  - `WIP-HLD-Angular-Portal.md`
- The pre-development task list is in `TODO.md`
- The repository documentation structure is defined in `docs/STRUCTURE.md`
- Today’s baseline is `2026-03-10`
- Goal: produce implementation-ready artefacts for one TODO item at a time, with enough precision to build directly, but without turning each artefact into a long-form design document

Your assignment:
Create the artefact for this TODO item:

### 3.6 Coding Conventions
- [ ] **.NET Conventions:**
  - [ ] Naming: PascalCase classes/methods, camelCase locals, _prefixed private fields
  - [ ] Project structure: feature-folder or layer-based (decide)
  - [ ] Async/await patterns, CancellationToken propagation
  - [ ] Result pattern vs exceptions for domain errors (decide)
  - [ ] Logging conventions: structured logging, what to log at Info/Warning/Error
- [ ] **Kotlin Conventions:**
  - [ ] Package naming aligned with project structure
  - [ ] Coroutine patterns: CoroutineScope management, Dispatcher usage
  - [ ] Room conventions: entity naming, DAO method naming
  - [ ] Ktor route organization
- [ ] **Shared Conventions:**
  - [ ] Date/time: UTC ISO 8601 everywhere, timezone only in display layer
  - [ ] Currency: minor units (cents) as Long/BigDecimal, never floating point
  - [ ] IDs: UUID v4 for middleware-generated IDs, preserve original FCC IDs as-is
  - [ ] API field naming: camelCase in JSON payloads

---
Core instructions:
1. Read only the relevant parts of `Requirements.md`, the applicable HLD document(s), `TODO.md`, and `docs/STRUCTURE.md`.
2. Select the correct target folder and filename based on `docs/STRUCTURE.md`.
3. Start the response by stating:
   - the chosen target file path
   - why that path is the correct location for this artefact
4. Convert the TODO item into a concrete, build-ready artefact. Do not restate the HLD or requirements except where needed for traceability.
5. Make explicit decisions where needed, but keep them limited to decisions that materially affect implementation.
6. If something is missing, either:
   - make the best concrete decision and justify it briefly, or
   - list it under `Open Questions` only if it truly cannot be closed from existing context
7. Keep the output concise. Prefer the smallest artefact that is still sufficient for implementation.
8. Use tables, schemas, state definitions, rules, and acceptance criteria instead of long explanatory prose.
9. Include cloud/backend, edge-agent, and portal impacts only if this TODO item actually affects them.
10. Call out prerequisite TODO items and downstream TODO items only when they are directly relevant.
11. If a machine-readable companion file is genuinely needed, identify it. Do not propose companion files by default.
12. Use stable filenames in lowercase kebab-case.

Compression rules:
- Prefer one canonical representation of information. Do not repeat the same content in prose, table, and code.
- Do not generate sample implementation classes in multiple languages unless the TODO item explicitly requires language-specific contracts.
- Do not include large example payloads, diagrams, or schemas unless they are the primary artefact for this TODO item.
- Limit `Decisions` to the few choices that materially affect implementation.
- Limit `Open Questions` to unresolved blockers only.
- `Out of scope`, `Dependencies`, and `Cross-Component Impact` should be short lists, not essays.
- `Output Files to Create` should list docs/contracts/schemas only. Do not speculate about `/src` or `/test` files unless the TODO item explicitly asks for implementation scaffolding.

Compact Contract Mode:
Use this for enums, shared types, canonical models, and field dictionaries.

Rules:
- Target 300 to 1,000 words.
- Keep descriptions to one line per enum value or field.
- Prefer a single primary table as the artefact.
- Inline validation, nullability, ownership, and notes as columns instead of separate prose sections where possible.
- Do not include diagrams.
- Do not include sample payloads.
- Do not include code unless the TODO explicitly requires a schema or machine-readable companion.
- `Key Decisions`, `Validation and Edge Cases`, and `Cross-Component Impact` should be minimal and may be `None` if not needed.

Required output format for Compact Contract Mode:

# Artefact Title

## 1. Output Location
- Target file path
- Optional companion files
- Why this location matches `docs/STRUCTURE.md`

## 2. Scope
- TODO item addressed
- In scope
- Out of scope

## 3. Source Traceability
- Requirements referenced: `REQ-x` only
- HLD sections referenced only
- Assumptions from TODO ordering/dependencies

## 4. Key Decisions
Use a short table with:
- Decision
- Why
- Impact

Write `None` if there are no material decisions.

## 5. Detailed Specification
Use only one or two compact structures, such as:
- enum table with `Enum`, `Value`, `Meaning`, `Usage Notes`
- field dictionary with `Field`, `Type`, `Required`, `Produced By`, `Description`
- model summary table with `Field`, `Type`, `Nullable`, `Validation`, `Notes`

## 6. Validation and Edge Cases
Keep this short. Prefer bullets or a tiny table.
Write `None` if everything is already clear from the main table.

## 7. Cross-Component Impact
Only include impacted components.
Write `None` if not needed.

## 8. Dependencies
- Prerequisites
- Downstream TODOs affected
- Recommended next implementation step

## 9. Open Questions
Include only true unresolved blockers.
If there are none, write `None`.

## 10. Acceptance Checklist
Create a concise checklist that can be used to mark this TODO item complete.

## 11. Output Files to Create
List only the concrete artefact files that should exist because of this work.

## 12. Recommended Next TODO
Name the next most logical TODO item to detail.

Full Spec Mode:
Use this for APIs, state machines, reconciliation logic, security flows, config behavior, database behavior, and error handling.

Rules:
- Target 800 to 1,800 words for most TODO items.
- Use only the structures needed for the item.
- Prefer compact tables over long prose.
- Only include diagrams, schemas, API fragments, or DDL when they are the actual artefact.

Required output format for Full Spec Mode:

# Artefact Title

## 1. Output Location
- Target file path
- Optional companion files
- Why this location matches `docs/STRUCTURE.md`

## 2. Scope
- TODO item addressed
- In scope
- Out of scope

## 3. Source Traceability
- Requirements referenced: `REQ-x` only
- HLD sections referenced only
- Assumptions from TODO ordering/dependencies

## 4. Key Decisions
Use a short table with:
- Decision
- Why
- Impact

## 5. Detailed Specification
Produce the actual artefact content required for this TODO item.
Use only the structures needed for this item, for example:
- state transition table
- API contract fragment
- config schema
- event schema
- DDL fragment
- rule set

## 6. Validation and Edge Cases
- Validations
- Failure handling
- Boundary cases
- Idempotency / dedup / concurrency notes if applicable

## 7. Cross-Component Impact
Only include impacted components.

## 8. Dependencies
- Prerequisites
- Downstream TODOs affected
- Recommended next implementation step

## 9. Open Questions
Include only true unresolved blockers.
For each:
- Question
- Recommendation
- Risk if deferred

If there are no open questions, write `None`.

## 10. Acceptance Checklist
Create a concise checklist that can be used to mark this TODO item complete.

## 11. Output Files to Create
List only the concrete artefact files that should exist because of this work.

## 12. Recommended Next TODO
Name the next most logical TODO item to detail.

Quality bar:
- No vague wording like “handle appropriately”, “as needed”, or “etc.”
- No duplicated content across sections
- Prefer exact field names, statuses, enum values, API paths, and transition rules
- Make recommendations explicit
- Follow `docs/STRUCTURE.md` for naming and placement
- Keep the artefact short enough that engineers will actually read it

Usage notes:
- Use one TODO item per prompt.
- For a naturally broad TODO item, split into multiple artefacts only if the TODO clearly decomposes.
- Prefer the human-readable spec first. Add a machine-readable companion only when it will be consumed directly by tooling or validators.
