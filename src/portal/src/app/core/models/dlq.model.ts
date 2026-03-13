// ── Enums ─────────────────────────────────────────────────────────────────────

export enum DeadLetterType {
  TRANSACTION = 'TRANSACTION',
  PRE_AUTH = 'PRE_AUTH',
  TELEMETRY = 'TELEMETRY',
  UNKNOWN = 'UNKNOWN',
}

export enum DeadLetterStatus {
  PENDING = 'PENDING',
  REPLAY_QUEUED = 'REPLAY_QUEUED',
  RETRYING = 'RETRYING',
  RESOLVED = 'RESOLVED',
  REPLAY_FAILED = 'REPLAY_FAILED',
  DISCARDED = 'DISCARDED',
}

export enum DeadLetterReason {
  VALIDATION_FAILURE = 'VALIDATION_FAILURE',
  NORMALIZATION_FAILURE = 'NORMALIZATION_FAILURE',
  DEDUPLICATION_ERROR = 'DEDUPLICATION_ERROR',
  ADAPTER_ERROR = 'ADAPTER_ERROR',
  PERSISTENCE_ERROR = 'PERSISTENCE_ERROR',
  UNKNOWN = 'UNKNOWN',
}

// ── Core model ────────────────────────────────────────────────────────────────

export interface DeadLetter {
  id: string;
  type: DeadLetterType;
  siteCode: string;
  legalEntityId: string;
  fccTransactionId: string | null;
  rawPayloadRef: string | null;
  failureReason: DeadLetterReason;
  errorCode: string;
  errorMessage: string;
  status: DeadLetterStatus;
  retryCount: number;
  lastRetryAt: string | null;
  discardReason: string | null;
  discardedBy: string | null;
  discardedAt: string | null;
  createdAt: string;
  updatedAt: string;
}

// ── Detail model (returned by GET /api/v1/dlq/:id) ────────────────────────────

export interface RetryHistoryEntry {
  attemptNumber: number;
  attemptedAt: string;
  outcome: 'SUCCESS' | 'FAILED';
  errorCode: string | null;
  errorMessage: string | null;
}

export interface DeadLetterDetail extends DeadLetter {
  rawPayload: Record<string, unknown> | string | null;
  retryHistory: RetryHistoryEntry[];
}

// ── Query params ──────────────────────────────────────────────────────────────

export interface DeadLetterQueryParams {
  legalEntityId: string;
  cursor?: string;
  pageSize?: number;
  siteCode?: string;
  failureReason?: DeadLetterReason;
  status?: DeadLetterStatus;
  from?: string;
  to?: string;
}

// ── Request / response ────────────────────────────────────────────────────────

export interface DiscardRequest {
  reason: string;
}

export interface RetryResult {
  id: string;
  queued: boolean;
  error: import('./common.model').ErrorResponse | null;
}

export interface BatchDiscardItem {
  id: string;
  reason: string;
}

export interface BatchRetryResult {
  succeeded: string[];
  failed: Array<{ id: string; error: string }>;
}

export interface BatchDiscardResult {
  succeeded: string[];
  failed: Array<{ id: string; error: string }>;
}
