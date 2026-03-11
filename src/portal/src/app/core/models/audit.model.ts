// ── Enum ──────────────────────────────────────────────────────────────────────

export enum EventType {
  TransactionIngested = 'TransactionIngested',
  TransactionDeduplicated = 'TransactionDeduplicated',
  TransactionSyncedToOdoo = 'TransactionSyncedToOdoo',
  PreAuthCreated = 'PreAuthCreated',
  PreAuthAuthorized = 'PreAuthAuthorized',
  PreAuthCompleted = 'PreAuthCompleted',
  PreAuthCancelled = 'PreAuthCancelled',
  PreAuthExpired = 'PreAuthExpired',
  ReconciliationMatched = 'ReconciliationMatched',
  ReconciliationVarianceFlagged = 'ReconciliationVarianceFlagged',
  ReconciliationApproved = 'ReconciliationApproved',
  AgentRegistered = 'AgentRegistered',
  AgentConfigUpdated = 'AgentConfigUpdated',
  AgentHealthReported = 'AgentHealthReported',
  ConnectivityChanged = 'ConnectivityChanged',
  BufferThresholdExceeded = 'BufferThresholdExceeded',
  MasterDataSynced = 'MasterDataSynced',
  ConfigChanged = 'ConfigChanged',
}

// ── Core model ────────────────────────────────────────────────────────────────

/**
 * Immutable audit event envelope.
 * Authoritative schema: `schemas/events/event-envelope.schema.json`
 */
export interface AuditEvent {
  eventId: string;
  eventType: EventType;
  schemaVersion: number;
  timestamp: string;
  source: string;
  correlationId: string;
  legalEntityId: string;
  siteCode: string | null;
  /** Event-specific data. Shape depends on eventType + schemaVersion. */
  payload: Record<string, unknown>;
}

// ── Query params ──────────────────────────────────────────────────────────────

export interface AuditEventQueryParams {
  legalEntityId: string;
  cursor?: string;
  pageSize?: number;
  correlationId?: string;
  eventType?: EventType;
  siteCode?: string;
  from?: string;
  to?: string;
}
