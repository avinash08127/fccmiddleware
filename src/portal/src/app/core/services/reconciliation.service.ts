import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  ReconciliationException,
  ReconciliationQueryParams,
  ReconciliationRecord,
} from '../models';
import { PagedResult } from '../models';

@Injectable({ providedIn: 'root' })
export class ReconciliationService {
  private readonly http = inject(HttpClient);

  getExceptions(params: ReconciliationQueryParams): Observable<PagedResult<ReconciliationException>> {
    let httpParams = new HttpParams();
    Object.entries(params).forEach(([k, v]) => {
      if (v != null && v !== '') httpParams = httpParams.set(k, String(v));
    });
    return this.http.get<PagedResult<ReconciliationException>>(
      '/api/v1/reconciliation/exceptions',
      { params: httpParams }
    );
  }

  getById(id: string): Observable<ReconciliationRecord> {
    return this.http.get<ReconciliationRecord>(`/api/v1/reconciliation/${id}`);
  }

  approve(id: string, reason: string): Observable<ReconciliationRecord> {
    return this.http.post<ReconciliationRecord>(
      `/api/v1/reconciliation/${id}/approve`,
      { reason }
    );
  }

  reject(id: string, reason: string): Observable<ReconciliationRecord> {
    return this.http.post<ReconciliationRecord>(
      `/api/v1/reconciliation/${id}/reject`,
      { reason }
    );
  }
}
