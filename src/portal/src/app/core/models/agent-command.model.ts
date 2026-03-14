// ── Agent Command types ──────────────────────────────────────────────────────

export type AgentCommandType = 'FORCE_CONFIG_PULL' | 'RESET_LOCAL_STATE' | 'DECOMMISSION';

export type AgentCommandStatus =
  | 'PENDING'
  | 'DELIVERY_HINT_SENT'
  | 'ACKED'
  | 'FAILED'
  | 'EXPIRED'
  | 'CANCELLED';

export interface CreateAgentCommandRequest {
  commandType: AgentCommandType;
  reason: string;
  payload?: unknown;
  expiresAt?: string;
}

export interface AgentCommandRow {
  commandId: string;
  deviceId: string;
  legalEntityId: string;
  siteCode: string;
  commandType: AgentCommandType;
  status: AgentCommandStatus;
  reason: string;
  payload: unknown | null;
  createdAt: string;
  expiresAt: string;
  createdByActorId: string | null;
  createdByActorDisplay: string | null;
}
