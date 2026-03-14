export interface GenerateBootstrapTokenRequest {
  siteCode: string;
  legalEntityId: string;
  environment?: string;
}

export interface GenerateBootstrapTokenResponse {
  tokenId: string;
  rawToken: string;
  expiresAt: string;
  siteCode: string;
}

export interface RevokeBootstrapTokenResponse {
  tokenId: string;
  revokedAt: string;
}

// ── Token history ────────────────────────────────────────────────────────────

export type BootstrapTokenEffectiveStatus = 'ACTIVE' | 'USED' | 'EXPIRED' | 'REVOKED';

export interface BootstrapTokenHistoryRow {
  tokenId: string;
  legalEntityId: string;
  siteCode: string;
  storedStatus: string;
  effectiveStatus: BootstrapTokenEffectiveStatus;
  createdAt: string;
  expiresAt: string;
  usedAt: string | null;
  usedByDeviceId: string | null;
  revokedAt: string | null;
  createdByActorId: string | null;
  createdByActorDisplay: string | null;
  revokedByActorId: string | null;
  revokedByActorDisplay: string | null;
}

export interface BootstrapTokenHistoryParams {
  legalEntityId: string;
  siteCode?: string;
  status?: BootstrapTokenEffectiveStatus;
  from?: string;
  to?: string;
  cursor?: string;
  pageSize?: number;
}
