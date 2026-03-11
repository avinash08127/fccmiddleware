# Phased Development Plan — Master Index

## Overview

This directory contains the complete phased development plan for the Forecourt Middleware Platform, organized by application. Each plan is accompanied by an agent system prompt that provides the context an AI coding agent needs before starting any task.

## Files

| File | Purpose |
|------|---------|
| **Agent Prompts** (prepend to every task assignment) | |
| `agent-prompt-cloud-backend.md` | System context for Cloud Backend (.NET) tasks |
| `agent-prompt-edge-agent.md` | System context for Edge Agent (Kotlin/Android) tasks |
| `agent-prompt-desktop-edge-agent.md` | System context for Desktop Edge Agent (.NET/Avalonia) tasks |
| `agent-prompt-angular-portal.md` | System context for Angular Portal tasks |
| **Development Plans** (detailed task breakdowns) | |
| `dev-plan-cloud-backend.md` | 24 tasks across 6 phases |
| `dev-plan-edge-agent.md` | 22 tasks across 5 phases |
| `dev-plan-desktop-edge-agent.md` | 25 tasks across 5 phases |
| `dev-plan-angular-portal.md` | 16 tasks across 4 phases |
| `dev-plan-virtual-lab.md` | FCC Simulator + Pump Simulator virtual lab plan |

## How to Use

### Assigning a Task to an Agent

1. **Start with the agent prompt**: Copy the contents of the relevant `agent-prompt-*.md` file as the system/context prompt
2. **Add the specific task**: Copy the task description (e.g., CB-1.2) from the relevant `dev-plan-*.md` file
3. **The agent should read the listed artifacts first**: Each task lists exactly which files to read before starting

### Task ID Convention

| Prefix | Application |
|--------|------------|
| `CB-` | Cloud Backend |
| `EA-` | Edge Agent (Android/Kotlin) |
| `DEA-` | Desktop Edge Agent (.NET/Avalonia) |
| `AP-` | Angular Portal |

### Sprint Mapping

| Sprint | Cloud Backend | Edge Agent (Android) | Desktop Edge Agent | Angular Portal |
|--------|--------------|------------|-------------------|----------------|
| 1–2 | Phase 0: Foundations (CB-0.1 → CB-0.6) | Phase 0: Foundations (EA-0.1 → EA-0.7) | Phase 0: Foundations (DEA-0.0 → DEA-0.8) | Phase 0: Scaffold (AP-0.1 → AP-0.4) |
| 3–5 | Phase 1: Core Ingestion (CB-1.1 → CB-1.8) | | | |
| 4–7 | | Phase 2: Core Agent (EA-2.1 → EA-2.7) | Phase 2: Core Agent (DEA-2.1 → DEA-2.7) | |
| 6–8 | Phase 3: Edge Integration (CB-3.1 → CB-3.6) | Phase 3: Cloud Integration (EA-3.1 → EA-3.6) | Phase 3: Cloud Integration (DEA-3.1 → DEA-3.6) | |
| 7–9 | Phase 4: Reconciliation (CB-4.1 → CB-4.4) | | Phase 4: GUI (DEA-4.1 → DEA-4.4) | |
| 8–11 | | | | Phase 5: Features (AP-5.1 → AP-5.9) |
| 10–12 | Phase 6: Hardening (CB-6.1 → CB-6.4) | Phase 6: Hardening (EA-6.1 → EA-6.3) | Phase 6: Hardening (DEA-6.1 → DEA-6.3) | Phase 6: Hardening (AP-6.1 → AP-6.2) |

## Cross-Application Dependencies

These are tasks that MUST be completed in one application before a task in another can start:

```
CB-1.2 (Ingestion API) ──────────────────────────────────────┐
CB-1.5 (Odoo Acknowledge API) ──┐                            │
CB-3.1 (Device Registration) ───┤                            │
                                 ├─► EA-3.1 (Cloud Upload)    │
                                 ├─► EA-3.2 (Status Poller)   │
                                 ├─► EA-3.3 (Config Poller)   │
                                 ├─► EA-3.4 (Telemetry)       │
                                 ├─► EA-3.5 (Registration)    │
                                 │                            │
                                 ├─► DEA-3.1 (Cloud Upload)   │
                                 ├─► DEA-3.2 (Status Poller)  │
                                 ├─► DEA-3.3 (Config Poller)  │
                                 ├─► DEA-3.4 (Telemetry)      │
                                 └─► DEA-3.5 (Registration)   │
                                                              │
CB-3.4 (SYNCED_TO_ODOO Status API) ──► EA-3.2 / DEA-3.2     │
CB-3.5 (Pre-Auth Forward API) ──────► EA-3.6 / DEA-3.6      │
CB-3.2 (Config API) ────────────────► EA-3.3 / DEA-3.3      │
CB-3.3 (Telemetry API) ─────────────► EA-3.4 / DEA-3.4      │
                                                              │
CB-1.x + CB-3.x + CB-4.x (all APIs) ──► AP-5.x (all portal) │
                                                              │
No Edge Agent dependency on Portal                            │
No Desktop Edge Agent dependency on Portal                    │
No Portal dependency on either Edge Agent                     │
EA and DEA are independent — no cross-dependencies            │
```

**Key insight**: Cloud Backend APIs must be ready before Edge Agent (Android or Desktop) integration and Portal features can connect. Edge Agents and Portal have NO direct dependencies on each other. The Android and Desktop Edge Agents are fully independent and can be developed in parallel.

## Team Allocation Recommendation

| Team/Developer | Focus | Sprint 1–5 | Sprint 6–8 | Sprint 9–12 |
|---------------|-------|-----------|-----------|------------|
| Backend Dev 1 | Cloud Core | CB-0.1→CB-0.4, CB-1.1→CB-1.3 | CB-3.1→CB-3.3, CB-4.1 | CB-4.2→CB-4.4, CB-6.1 |
| Backend Dev 2 | Cloud APIs | CB-0.2→CB-0.3, CB-1.4→CB-1.8 | CB-3.4→CB-3.6 | CB-6.2→CB-6.4 |
| Mobile Dev | Edge Agent (Android) | EA-0.1→EA-0.7 | EA-2.1→EA-2.7, EA-3.1→EA-3.2 | EA-3.3→EA-3.6, EA-6.x |
| Desktop Dev | Desktop Edge Agent | DEA-0.0→DEA-0.8 | DEA-2.1→DEA-2.7, DEA-3.1→DEA-3.2 | DEA-3.3→DEA-3.6, DEA-4.x, DEA-6.x |
| Frontend Dev | Angular Portal | AP-0.1→AP-0.4 | AP-5.1→AP-5.3 | AP-5.4→AP-5.9, AP-6.x |
| DevOps/Infra | CI/CD + Infra | CB-0.5, EA-0.7, DEA-0.7, AP-0.4 | Environments, monitoring | CB-6.3, production setup |

## External Dependencies

| Dependency | Required By | Blocking Tasks |
|-----------|------------|----------------|
| DOMS FCC documentation | Sprint 3 | CB-1.1, EA-2.1, DEA-2.1 |
| Urovo i9100 hardware | Sprint 4 | EA-2.x (can use emulator until then) |
| Site PC/Mac for testing | Sprint 4 | DEA-2.x (can use dev machine until then) |
| Azure Entra app registration | Sprint 8 | AP-5.1 |
| AWS account provisioned | Sprint 1 | CB-0.2 (can use local Docker until then) |
| Sure MDM access | Sprint 10 | EA-6.1 (field testing) |
| Velopack release endpoint | Sprint 2 | DEA-0.8 (can use local file source until then) |
