import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  Transaction,
  TransactionDetail,
  TransactionQueryParams,
  AcknowledgeItem,
  AcknowledgeBatchResponse,
} from '../models';
import { PagedResult } from '../models';

@Injectable({ providedIn: 'root' })
export class TransactionService {
  private readonly http = inject(HttpClient);

  getTransactions(params: TransactionQueryParams): Observable<PagedResult<Transaction>> {
    let httpParams = new HttpParams();
    Object.entries(params).forEach(([key, value]) => {
      if (value != null && value !== '') httpParams = httpParams.set(key, String(value));
    });
    return this.http.get<PagedResult<Transaction>>('/api/v1/ops/transactions', { params: httpParams });
  }

  getTransactionById(id: string): Observable<TransactionDetail> {
    return this.http.get<TransactionDetail>(`/api/v1/ops/transactions/${id}`);
  }

  acknowledgeTransactions(
    acknowledgements: AcknowledgeItem[]
  ): Observable<AcknowledgeBatchResponse> {
    return this.http.post<AcknowledgeBatchResponse>('/api/v1/ops/transactions/acknowledge', {
      acknowledgements,
    });
  }
}
