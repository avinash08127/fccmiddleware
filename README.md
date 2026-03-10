# Forecourt Middleware Architecture

Version: 3.0 Status: Approved Architecture Decision: Option B + Option C (Selective) + Option D

## Overview

This document defines the architecture for the Forecourt Middleware
Platform integrating Odoo POS with multiple Forecourt Controllers
(FCCs).

Supported countries include: - Malawi - Tanzania - Botswana - Zambia -
Namibia

Supported FCC vendors: - DOMS - Radix - Advatec - Petronite

The middleware supports: - Pre-Authorization fuel sales - Post-dispense
transaction ingestion - Solicited transaction polling - Unsolicited
transaction push - Fiscalized transaction capture - Multi-country
configuration - Internet and Local LAN connectivity - Pump / nozzle
mapping

------------------------------------------------------------------------

# Final Architecture Decision

Selected Architecture: Option B + Option C (Selective) + Option D

Hybrid Adapter Middleware + Selective Event Streaming + Edge Site Agent

This approach provides:

-   Global centralized middleware
-   Vendor-specific FCC adapters
-   Selective event-driven backbone for transaction flows
-   Local site connectivity agents
-   Full configuration-driven controller integration
-   Scalable deployment for new countries
-   Durable audit trail for fiscal compliance

Event streaming (Option C) is applied selectively — not as a blanket
pattern — to the following areas:

-   Edge-to-cloud transaction flow (store-and-forward)
-   Unsolicited transaction capture from FCCs
-   Transaction reconciliation between Odoo and FCCs
-   Fiscal and audit event logging

------------------------------------------------------------------------

# Architecture Decision Summary

  Option     Architecture                   Status
  ---------- ------------------------------ ----------------------
  Option A   Direct Integration             Archived
  Option B   Adapter-based Middleware       Selected
  Option C   Event Streaming Architecture   Selected (Selective)
  Option D   Edge Site Agent                Selected

------------------------------------------------------------------------

# High Level Architecture

Odoo POS -\> Middleware API -\> Orchestration Engine -\> FCC Adapters
-\> Transport Layer -\> Edge Agent -\> Forecourt Controller

Event Bus (RabbitMQ / Azure Service Bus) underpins:

-   Orchestration Engine \<-\> Edge Agent (command/event relay)
-   Edge Agent -\> Event Bus (unsolicited transactions, buffered replay)
-   Event Bus -\> Transaction Store (audit log, reconciliation)

------------------------------------------------------------------------

# Core Components

## Middleware API Layer

Provides APIs used by Odoo.

Example endpoints:

POST /preauth POST /preauth/{id}/cancel GET /preauth/{id} GET
/transactions/{id} POST /transactions/fetch

Responsibilities: - Validate incoming requests - Resolve site
configuration - Dispatch to orchestration layer - Provide idempotent
APIs

------------------------------------------------------------------------

## Forecourt Orchestration Engine

Responsible for:

-   Controller resolution
-   Connectivity selection
-   Adapter invocation
-   Transaction correlation
-   Retry logic
-   Timeout management

------------------------------------------------------------------------

## FCC Adapter Layer

Each FCC vendor is implemented as a separate adapter.

Example adapters:

Adapters/ DOMS Radix Advatec Petronite

Example adapter interface:

public interface IForecourtControllerAdapter { Task AuthorizeAsync();
Task FetchTransactionsAsync(); Task CancelAuthorizationAsync(); Task
GetPumpStatusAsync(); }

------------------------------------------------------------------------

## Event Bus Layer

Selective event-driven backbone using RabbitMQ / Azure Service Bus.

Applied to:

-   Edge-to-cloud transaction relay (store-and-forward pattern)
-   Unsolicited transaction ingestion from FCCs
-   Transaction reconciliation events between Odoo and FCCs
-   Fiscal and audit event logging (immutable event trail)

Not applied to:

-   Synchronous pre-auth request/response flows (remain REST-based)
-   Admin portal queries and configuration management

------------------------------------------------------------------------

## Site Edge Agent

A lightweight .NET worker deployed at station network.

Responsibilities:

-   Communicate with FCC via LAN
-   Relay commands from cloud middleware
-   Capture unsolicited transactions
-   Buffer transactions if internet fails
-   Replay when connectivity resumes
-   Publish transaction events to the event bus

------------------------------------------------------------------------

# Connectivity Modes

DirectInternet VPNPrivateNetwork EdgeAgentLAN Hybrid

------------------------------------------------------------------------

# Canonical Data Models

## Pre Authorization

{ "orderId": "ODOO-10001", "siteCode": "MW-BT001", "pumpNumber": 4,
"nozzleNumber": 2, "productCode": "AGO", "requestedAmount": 20000,
"vehicleNumber": "BW1234", "taxId": "TIN123456" }

------------------------------------------------------------------------

## Dispense Transaction

{ "transactionId": "FCC-999", "siteCode": "TZ-DAR01", "pumpNumber": 3,
"nozzleNumber": 1, "productCode": "PMS", "volume": 35.6, "amount":
100000, "fiscalReceipt": "FR-123456" }

------------------------------------------------------------------------

# Architecture Decision Record

## ADR-001 Direct Integration

Description: Odoo integrates directly with FCC.

Reason Rejected: - Tight coupling - Hard to scale across countries -
Frequent ERP modifications

Decision: Archived

------------------------------------------------------------------------

## ADR-002 Event Streaming Architecture (Selective)

Description: Selective event-driven backbone applied to transaction
flows, unsolicited capture, reconciliation, and fiscal audit logging.

Originally rejected as "over-engineered" when proposed as a blanket
pattern. Revisited and adopted selectively because:

-   The tech stack already commits to RabbitMQ / Azure Service Bus
-   Edge agent buffer-and-replay is event sourcing by nature
-   Unsolicited FCC transactions are inherently event-driven
-   Fiscal compliance benefits from an immutable event trail
-   Modern tooling (MassTransit, Azure Service Bus) reduces operational burden

Decision: Selected (Selective)

------------------------------------------------------------------------

# Decision Rationale

Option B + C (Selective) + D selected due to:

-   Multi-country scalability
-   Vendor abstraction
-   Offline site support
-   Reliable transaction buffering
-   Durable event trail for fiscal compliance and audit
-   Event-driven decoupling for unsolicited transactions and reconciliation
-   Faster rollout across Africa

Modern DevOps, managed messaging services, and AI-assisted development
significantly reduce the operational complexity that originally led to
Option C being archived.

------------------------------------------------------------------------

# Technology Stack

Backend: .NET 10 / ASP.NET Core Worker Services PostgreSQL Redis RabbitMQ
/ Azure Service Bus

Frontend: Angular Admin Portal

Edge Agent: .NET Worker Service SQLite local buffer

------------------------------------------------------------------------

# Implementation Phases

Phase 1: Core Middleware + DOMS adapter

Phase 2: Edge agent + transaction polling

Phase 3: Radix / Advatec adapters

Phase 4: Reconciliation dashboards
