import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  AgentCommandRow,
  CreateAgentCommandRequest,
} from '../models/agent-command.model';
import { PagedResult } from '../models/common.model';
import { buildHttpParams } from './http-params.util';

@Injectable({ providedIn: 'root' })
export class AgentCommandService {
  private readonly http = inject(HttpClient);

  createCommand(deviceId: string, req: CreateAgentCommandRequest): Observable<AgentCommandRow> {
    return this.http.post<AgentCommandRow>(
      `/api/v1/admin/agents/${deviceId}/commands`,
      req,
    );
  }

  getCommands(
    deviceId: string,
    params: { cursor?: string; pageSize?: number } = {},
  ): Observable<PagedResult<AgentCommandRow>> {
    return this.http.get<PagedResult<AgentCommandRow>>(
      `/api/v1/agents/${deviceId}/commands`,
      { params: buildHttpParams(params) },
    );
  }
}
