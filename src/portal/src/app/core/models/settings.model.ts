// ── Settings ──────────────────────────────────────────────────────────────────

export interface ReconciliationToleranceSettings {
  /** Variance threshold in basis points below which auto-match occurs. */
  autoMatchThresholdBps: number;
  /** Variance threshold in basis points above which the record is flagged. */
  flagThresholdBps: number;
  /** Absolute variance in minor currency units that always triggers a flag. */
  absoluteThresholdMinorUnits: number;
}

export interface AlertChannelSettings {
  emailEnabled: boolean;
  emailRecipients: string[];
  slackEnabled: boolean;
  slackWebhookRef: string | null;
}

export interface RetentionSettings {
  /** Days to retain raw payloads in blob storage. */
  rawPayloadRetentionDays: number;
  /** Days to retain audit events. */
  auditEventRetentionDays: number;
  /** Days to retain dead-letter records after resolution. */
  deadLetterRetentionDays: number;
}

export interface SystemSettings {
  reconciliation: ReconciliationToleranceSettings;
  alerts: AlertChannelSettings;
  retention: RetentionSettings;
  updatedAt: string | null;
  updatedBy: string | null;
}

export interface UpdateSettingsRequest {
  reconciliation?: Partial<ReconciliationToleranceSettings>;
  alerts?: Partial<AlertChannelSettings>;
  retention?: Partial<RetentionSettings>;
}
