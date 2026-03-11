// ── Global Defaults ──────────────────────────────────────────────────────────

export interface ToleranceDefaults {
  amountTolerancePercent: number;
  amountToleranceAbsoluteMinorUnits: number;
  timeWindowMinutes: number;
  stalePendingThresholdDays: number;
}

export interface RetentionDefaults {
  archiveRetentionMonths: number;
  outboxCleanupDays: number;
  rawPayloadRetentionDays: number;
  auditEventRetentionDays: number;
  deadLetterRetentionDays: number;
}

export interface GlobalDefaults {
  tolerance: ToleranceDefaults;
  retention: RetentionDefaults;
}

// ── Per-Legal-Entity Overrides ───────────────────────────────────────────────

export interface LegalEntityOverride {
  legalEntityId: string;
  legalEntityName: string;
  legalEntityCode: string;
  amountTolerancePercent: number | null;
  amountToleranceAbsoluteMinorUnits: number | null;
  timeWindowMinutes: number | null;
  stalePendingThresholdDays: number | null;
}

// ── Alert Configuration ──────────────────────────────────────────────────────

export interface AlertThreshold {
  alertKey: string;
  label: string;
  threshold: number;
  unit: string;
  evaluationWindowMinutes: number;
}

export interface AlertConfiguration {
  thresholds: AlertThreshold[];
  emailRecipientsHigh: string[];
  emailRecipientsCritical: string[];
  renotifyIntervalHours: number;
  autoResolveHealthyCount: number;
}

// ── Aggregated Settings ──────────────────────────────────────────────────────

export interface SystemSettings {
  globalDefaults: GlobalDefaults;
  legalEntityOverrides: LegalEntityOverride[];
  alerts: AlertConfiguration;
  updatedAt: string | null;
  updatedBy: string | null;
}

// ── Request DTOs ─────────────────────────────────────────────────────────────

export interface UpdateGlobalDefaultsRequest {
  tolerance: Partial<ToleranceDefaults>;
  retention: Partial<RetentionDefaults>;
}

export interface UpsertLegalEntityOverrideRequest {
  legalEntityId: string;
  amountTolerancePercent: number | null;
  amountToleranceAbsoluteMinorUnits: number | null;
  timeWindowMinutes: number | null;
  stalePendingThresholdDays: number | null;
}

export interface UpdateAlertConfigurationRequest {
  thresholds: { alertKey: string; threshold: number; evaluationWindowMinutes: number }[];
  emailRecipientsHigh: string[];
  emailRecipientsCritical: string[];
  renotifyIntervalHours: number;
  autoResolveHealthyCount: number;
}
