import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  DeadLetter,
  DeadLetterDetail,
  DeadLetterQueryParams,
  DiscardRequest,
  RetryResult,
  BatchDiscardItem,
  BatchRetryResult,
} from '../models';
import { PagedResult } from '../models';

@Injectable({ providedIn: 'root' })
export class DlqService {
  private readonly http = inject(HttpClient);

  getDeadLetters(params: DeadLetterQueryParams): Observable<PagedResult<DeadLetter>> {
    return this.http.get<PagedResult<DeadLetter>>('/api/v1/dlq', {
      params: params as unknown as Record<string, string>,
    });
  }

  getDeadLetterById(id: string): Observable<DeadLetterDetail> {
    return this.http.get<DeadLetterDetail>(`/api/v1/dlq/${id}`);
  }

  retry(id: string): Observable<RetryResult> {
    return this.http.post<RetryResult>(`/api/v1/dlq/${id}/retry`, {});
  }

  discard(id: string, reason: string): Observable<void> {
    return this.http.post<void>(`/api/v1/dlq/${id}/discard`, { reason } satisfies DiscardRequest);
  }

  retryBatch(ids: string[]): Observable<BatchRetryResult> {
    return this.http.post<BatchRetryResult>('/api/v1/dlq/retry-batch', { ids });
  }

  discardBatch(items: BatchDiscardItem[]): Observable<void> {
    return this.http.post<void>('/api/v1/dlq/discard-batch', { items });
  }
}
