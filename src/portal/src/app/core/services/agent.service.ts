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
      params: params as unknown as Record<string, string>,
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
}

export interface DiagnosticLogsResponse {
  deviceId: string;
  batches: DiagnosticLogBatch[];
}

export interface DiagnosticLogBatch {
  id: string;
  uploadedAtUtc: string;
  logEntries: string[];
}
