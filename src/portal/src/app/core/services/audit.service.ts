import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { AuditEvent, AuditEventQueryParams } from '../models';
import { PagedResult } from '../models';

@Injectable({ providedIn: 'root' })
export class AuditService {
  private readonly http = inject(HttpClient);

  getAuditEvents(params: AuditEventQueryParams): Observable<PagedResult<AuditEvent>> {
    return this.http.get<PagedResult<AuditEvent>>('/api/v1/audit/events', {
      params: params as unknown as Record<string, string>,
    });
  }
}
