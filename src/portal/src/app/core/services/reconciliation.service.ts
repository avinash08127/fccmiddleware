import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { switchMap } from 'rxjs/operators';
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
    Object.entries(params).forEach(([key, value]) => {
      if (value == null || value === '') return;
      const httpKey = key === 'reconciliationStatus' ? 'status' : key;
      httpParams = httpParams.set(httpKey, String(value));
    });
    return this.http.get<PagedResult<ReconciliationException>>(
      '/api/v1/ops/reconciliation/exceptions',
      { params: httpParams }
    );
  }

  getById(id: string): Observable<ReconciliationRecord> {
    return this.http.get<ReconciliationRecord>(`/api/v1/ops/reconciliation/${id}`);
  }

  approve(id: string, reason: string): Observable<ReconciliationRecord> {
    return this.http.post(
      `/api/v1/ops/reconciliation/${id}/approve`,
      { reason }
    ).pipe(switchMap(() => this.getById(id)));
  }

  reject(id: string, reason: string): Observable<ReconciliationRecord> {
    return this.http.post(
      `/api/v1/ops/reconciliation/${id}/reject`,
      { reason }
    ).pipe(switchMap(() => this.getById(id)));
  }
}
