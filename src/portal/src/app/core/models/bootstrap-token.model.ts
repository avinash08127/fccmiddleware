export interface GenerateBootstrapTokenRequest {
  siteCode: string;
  legalEntityId: string;
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
