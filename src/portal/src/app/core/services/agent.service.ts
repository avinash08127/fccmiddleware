import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  AgentAuditEvent,
  AgentHealthSummary,
  AgentRegistration,
  AgentTelemetry,
} from '../models';
import { PagedResult } from '../models';
import { buildHttpParams } from './http-params.util';

export interface AgentQueryParams {
  legalEntityId: string;
  cursor?: string;
  pageSize?: number;
  siteCode?: string;
  status?: string;
  connectivityState?: string;
}

@Injectable({ providedIn: 'root' })
export class AgentService {
  private readonly http = inject(HttpClient);

  getAgents(params: AgentQueryParams): Observable<PagedResult<AgentHealthSummary>> {
    return this.http.get<PagedResult<AgentHealthSummary>>('/api/v1/agents', {
      params: buildHttpParams(params),
    });
  }

  getAgentById(id: string): Observable<AgentRegistration> {
    return this.http.get<AgentRegistration>(`/api/v1/agents/${id}`);
  }

  getAgentTelemetry(id: string): Observable<AgentTelemetry> {
    return this.http.get<AgentTelemetry>(`/api/v1/agents/${id}/telemetry`);
  }

  getAgentEvents(id: string, limit = 20): Observable<AgentAuditEvent[]> {
    return this.http.get<AgentAuditEvent[]>(`/api/v1/agents/${id}/events`, {
      params: { limit: limit.toString() },
    });
  }

  getAgentDiagnosticLogs(id: string, maxBatches = 10): Observable<DiagnosticLogsResponse> {
    return this.http.get<DiagnosticLogsResponse>(`/api/v1/agents/${id}/diagnostic-logs`, {
      params: { maxBatches: maxBatches.toString() },
    });
  }

  decommissionAgent(deviceId: string, reason: string): Observable<DecommissionResponse> {
    return this.http.post<DecommissionResponse>(`/api/v1/admin/agent/${deviceId}/decommission`, { reason });
  }

  approveSuspiciousAgent(deviceId: string, reason: string): Observable<SuspiciousRegistrationReviewResponse> {
    return this.http.post<SuspiciousRegistrationReviewResponse>(`/api/v1/admin/agent/${deviceId}/approve`, { reason });
  }

  rejectSuspiciousAgent(deviceId: string, reason: string): Observable<SuspiciousRegistrationReviewResponse> {
    return this.http.post<SuspiciousRegistrationReviewResponse>(`/api/v1/admin/agent/${deviceId}/reject`, { reason });
  }
}

export interface DecommissionResponse {
  deviceId: string;
  deactivatedAt: string;
}

export interface DiagnosticLogsResponse {
  deviceId: string;
  batches: DiagnosticLogBatch[];
}

export interface SuspiciousRegistrationReviewResponse {
  deviceId: string;
  status: string;
  updatedAt: string;
}

export interface DiagnosticLogBatch {
  id: string;
  uploadedAtUtc: string;
  logEntries: string[];
}
