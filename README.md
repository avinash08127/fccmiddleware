# Forecourt Middleware Platform

A cloud-native middleware platform that integrates **Odoo ERP/POS** with **Forecourt Controllers (FCCs)** across 2,000+ fuel stations in sub-Saharan Africa. The platform handles fuel transaction ingestion, pre-authorization, fiscalization, and offline-resilient operations — ensuring no transaction is ever lost, even when internet connectivity fails.

---

## The Problem

Fuel retail operations in Africa face a unique challenge: stations must keep operating when internet drops — which happens frequently. Fuel pumps are controlled by hardware (FCCs) that sit on a local network. When internet goes down, cloud-based ERP systems like Odoo lose visibility into what's being dispensed. Transactions get lost, reconciliation breaks, and revenue leaks.

## The Solution

This middleware bridges that gap with a three-tier architecture:

1. **Cloud Backend (AWS)** — Central transaction store, deduplication, reconciliation, and Odoo integration
2. **Edge Agent (Android HHT)** — Runs on the same handheld device as Odoo POS; talks to the FCC over LAN; buffers everything locally when internet is down; replays when connectivity returns
3. **Admin Portal (Angular)** — Operational dashboard for monitoring 2,000 sites, reviewing reconciliation exceptions, and managing configuration

---

## Scale

| Dimension | Scale |
|-----------|-------|
| Countries | 5 (Malawi, Tanzania, Botswana, Zambia, Namibia) |
| Sites | 2,000+ |
| Pumps per site | 4–5 |
| Peak transactions/day | ~2,000,000 |
| Offline buffer per device | 30+ days (30,000 transactions) |
| FCC vendors | DOMS (MVP), Radix, Advatec, Petronite |

---

## Architecture

**Selected approach:** Adapter-based Middleware + Selective Event Streaming + Edge Site Agent

```
[Forecourt Controller]
         |
         +-- Push (primary) -----------------------> [Cloud Middleware (AWS)]
         |                                                   ^   |
         |                                          pre-auth |   | SYNCED_TO_ODOO
         |                                          forward  |   v
         +-- LAN poll (catch-up) <---- [Edge Agent] <-------+
                                            |
                                    localhost:8585
                                            |
                                      [Odoo POS]
```

**Key flows:**
- **Normal Orders** — FCC pushes directly to cloud; Edge Agent polls LAN as safety net; Odoo polls cloud for pending transactions
- **Pre-Auth Orders** — Odoo POS sends pre-auth to Edge Agent over LAN (works offline); Edge Agent authorizes pump via FCC; cloud reconciles actual vs authorized amount
- **Offline Mode** — Edge Agent buffers locally; Odoo POS switches to Edge Agent's local API; on reconnect, everything replays to cloud with full deduplication

---

## Technology Stack

| Layer | Technology |
|-------|-----------|
| Cloud Backend | .NET 10, ASP.NET Core, PostgreSQL (Aurora), Redis, RabbitMQ (Amazon MQ) |
| Cloud Infra | AWS — ECS Fargate, Aurora, ElastiCache, S3, CloudFront, Cognito |
| Edge Agent | Native Kotlin/Java Android app (Urovo i9100, Android 12) with SQLite |
| Admin Portal | Angular 18+, hosted on CloudFront + S3 |
| Master Data Sync | Odoo → Databricks → Middleware |
| Device Management | Sure MDM |

---

## Key Design Principles

- **Offline-first** — The Edge Agent assumes internet will fail. LAN operations (pre-auth, FCC polling) never depend on internet.
- **No transaction left behind** — Dual-path ingestion (FCC push + Edge Agent poll) with cloud-side deduplication ensures completeness.
- **Odoo pulls, middleware stores** — The middleware never pushes orders into Odoo. Odoo polls for pending transactions and creates orders on its own terms.
- **Adapter pattern** — Each FCC vendor is a separate adapter. Adding a new vendor is a code change (new adapter), not a config change.
- **Multi-tenancy** — All data partitioned by legal entity (country). Row-level isolation.

---

## Repository Structure

| Document | Description |
|----------|-------------|
| [Requirements.md](Requirements.md) | Full requirements specification (REQ-1 through REQ-17) with data models, business rules, and acceptance criteria |
| [HighLevelRequirements.md](HighLevelRequirements.md) | Condensed requirements overview with open questions (all resolved) and MVP scope |
| [FlowDiagrams.md](FlowDiagrams.md) | 7 scenario flow diagrams covering online, offline, pre-auth, multi-HHT, and cleanup flows |
| [WIP-HLD-Backend.md](WIP-HLD-Backend.md) | High-level design — Cloud Backend (AWS deployment, components, data architecture) |
| [WIP-HLD-Edge-Agent.md](WIP-HLD-Edge-Agent.md) | High-level design — Android Edge Agent (Kotlin/Java, SQLite, Ktor, FCC adapters) |
| [WIP-HLD-Admin-Portal.md](WIP-HLD-Admin-Portal.md) | High-level design — Angular Admin Portal (CloudFront + S3, Cognito auth, feature modules) |

---

## Implementation Phases

| Phase | Scope | Status |
|-------|-------|--------|
| **Phase 1** | Core cloud middleware + DOMS adapter + transaction ingestion | Planned |
| **Phase 2** | Edge Agent (Android) + offline buffer + LAN polling + pre-auth | Planned |
| **Phase 3** | Radix, Advatec, Petronite adapters | Planned |
| **Phase 4** | Angular Admin Portal + reconciliation dashboards | Planned |

---

## Operating Modes

Sites operate in one of four ownership models — **COCO**, **CODO**, **DODO**, **DOCO** — which determine tax handling and operator identification. Sites can be **connected** (FCC present, automated) or **disconnected** (no FCC, manual operations).

Fiscalization is handled per-country: FCC-direct (Tanzania), external integration (Malawi/MRA), or none — configurable at legal entity and site level.
