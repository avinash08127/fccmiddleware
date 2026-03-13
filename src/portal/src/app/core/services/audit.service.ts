import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { AuditEvent, AuditEventQueryParams } from '../models';
import { PagedResult } from '../models';

@Injectable({ providedIn: 'root' })
export class AuditService {
  private readonly http = inject(HttpClient);

  getAuditEvents(params: AuditEventQueryParams): Observable<PagedResult<AuditEvent>> {
    let p = new HttpParams().set('legalEntityId', params.legalEntityId);
    if (params.cursor) p = p.set('cursor', params.cursor);
    if (params.pageSize != null) p = p.set('pageSize', String(params.pageSize));
    if (params.correlationId) p = p.set('correlationId', params.correlationId);
    if (params.eventTypes?.length) {
      params.eventTypes.forEach((t) => (p = p.append('eventTypes', t)));
    }
    if (params.siteCode) p = p.set('siteCode', params.siteCode);
    if (params.adapterKey) p = p.set('adapterKey', params.adapterKey);
    if (params.from) p = p.set('from', params.from);
    if (params.to) p = p.set('to', params.to);
    return this.http.get<PagedResult<AuditEvent>>('/api/v1/audit/events', { params: p });
  }

  getAuditEventById(eventId: string): Observable<AuditEvent> {
    return this.http.get<AuditEvent>(`/api/v1/audit/events/${eventId}`);
  }
}
