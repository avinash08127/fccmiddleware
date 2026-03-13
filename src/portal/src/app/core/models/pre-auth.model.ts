// ── Enum ──────────────────────────────────────────────────────────────────────

export enum PreAuthStatus {
  PENDING = 'PENDING',
  AUTHORIZED = 'AUTHORIZED',
  DISPENSING = 'DISPENSING',
  COMPLETED = 'COMPLETED',
  CANCELLED = 'CANCELLED',
  EXPIRED = 'EXPIRED',
  FAILED = 'FAILED',
}

// ── Core model ────────────────────────────────────────────────────────────────

export interface PreAuthRecord {
  id: string;
  siteCode: string;
  odooOrderId: string;
  pumpNumber: number;
  nozzleNumber: number;
  productCode: string;
  /** Authorised amount in minor currency units. */
  requestedAmount: number;
  /** Price per litre in minor currency units at time of authorisation. */
  unitPrice: number;
  currency: string;
  status: PreAuthStatus;
  requestedAt: string;
  expiresAt: string;
  createdAt: string;
  updatedAt: string;
  schemaVersion: number;
  vehicleNumber: string | null;
  customerName: string | null;
  customerTaxId: string | null;
  customerBusinessName: string | null;
  attendantId: string | null;
  fccCorrelationId: string | null;
  fccAuthorizationCode: string | null;
  matchedFccTransactionId: string | null;
  /** Actual dispensed amount in minor currency units. Null until COMPLETED. */
  actualAmount: number | null;
  /** Actual dispensed volume in millilitres (1 L = 1,000). Null until COMPLETED. */
  actualVolume: number | null;
  /** actualAmount − authorizedAmount in minor units. Null until COMPLETED. */
  amountVariance: number | null;
  /** ABS(amountVariance) / authorizedAmount × 10 000. Null until COMPLETED. */
  varianceBps: number | null;
  authorizedAt: string | null;
  dispensingAt: string | null;
  completedAt: string | null;
  cancelledAt: string | null;
  expiredAt: string | null;
  failedAt: string | null;
}

// ── Query params ──────────────────────────────────────────────────────────────

export interface PreAuthQueryParams {
  legalEntityId: string;
  cursor?: string;
  pageSize?: number;
  siteCode?: string;
  status?: PreAuthStatus;
  from?: string;
  to?: string;
  odooOrderId?: string;
}

// ── Forward request/response (portal reference — mirrors Edge Agent API call) ─

export interface PreAuthForwardRequest {
  siteCode: string;
  odooOrderId: string;
  pumpNumber: number;
  nozzleNumber: number;
  productCode: string;
  requestedAmount: number;
  unitPrice: number;
  currency: string;
  status: PreAuthStatus;
  requestedAt: string;
  expiresAt: string;
  fccCorrelationId?: string | null;
  fccAuthorizationCode?: string | null;
  vehicleNumber?: string | null;
  customerName?: string | null;
  customerTaxId?: string | null;
  customerBusinessName?: string | null;
  attendantId?: string | null;
}

export interface PreAuthForwardResponse {
  id: string;
  status: PreAuthStatus;
  siteCode: string;
  odooOrderId: string;
  createdAt: string | null;
  updatedAt: string | null;
}
