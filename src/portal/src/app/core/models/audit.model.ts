export const KNOWN_AUDIT_EVENT_TYPES = [
  'TransactionIngested',
  'TransactionDeduplicated',
  'TransactionSyncedToOdoo',
  'PreAuthCreated',
  'PreAuthAuthorized',
  'PreAuthCompleted',
  'PreAuthCancelled',
  'PreAuthExpired',
  'ReconciliationMatched',
  'ReconciliationVarianceFlagged',
  'ReconciliationApproved',
  'AgentRegistered',
  'AgentConfigUpdated',
  'AgentHealthReported',
  'ConnectivityChanged',
  'BufferThresholdExceeded',
  'MasterDataSynced',
  'ConfigChanged',
  'SITE_CONFIG_UPDATED',
  'AdapterDefaultConfigUpdated',
  'SiteAdapterOverrideSet',
  'SiteAdapterOverrideCleared',
  'SiteAdapterOverrideResetToDefault',
] as const;

export type EventType = string;

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
  /** Multi-select: send each value as a repeated `eventTypes` query param. */
  eventTypes?: EventType[];
  siteCode?: string;
  adapterKey?: string;
  from?: string;
  to?: string;
}
