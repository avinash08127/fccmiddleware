// ── Enums ─────────────────────────────────────────────────────────────────────

export enum DeadLetterStatus {
  PENDING = 'PENDING',
  RETRYING = 'RETRYING',
  RESOLVED = 'RESOLVED',
  DISCARDED = 'DISCARDED',
}

export enum DeadLetterReason {
  VALIDATION_FAILURE = 'VALIDATION_FAILURE',
  DEDUPLICATION_ERROR = 'DEDUPLICATION_ERROR',
  ADAPTER_ERROR = 'ADAPTER_ERROR',
  PERSISTENCE_ERROR = 'PERSISTENCE_ERROR',
  UNKNOWN = 'UNKNOWN',
}

// ── Core model ────────────────────────────────────────────────────────────────

export interface DeadLetter {
  id: string;
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

// ── Query params ──────────────────────────────────────────────────────────────

export interface DeadLetterQueryParams {
  legalEntityId: string;
  cursor?: string;
  pageSize?: number;
  siteCode?: string;
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
