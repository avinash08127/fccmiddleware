import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { CardModule } from 'primeng/card';
import { LoadingSpinnerComponent } from '../../../../shared/components/loading-spinner/loading-spinner.component';
import { IngestionHealthData } from '../../dashboard.model';

@Component({
  selector: 'app-ingestion-health',
  standalone: true,
  imports: [CommonModule, CardModule, LoadingSpinnerComponent],
  template: `
    <p-card styleClass="h-full">
      <ng-template pTemplate="title">
        <span class="widget-title">
          <i class="pi pi-heart-fill"></i> Ingestion Health
        </span>
      </ng-template>
      <div class="health-wrapper">
        <app-loading-spinner [visible]="loading" />
        @if (!loading && error) {
          <div class="widget-error">
            <i class="pi pi-exclamation-triangle"></i>
            <span>{{ error }}</span>
          </div>
        }
        @if (!loading && !error && data) {
          <div class="metrics-grid">
            <div class="metric">
              <span class="metric__value">{{ data.transactionsPerMinute | number: '1.0-1' }}</span>
              <span class="metric__label">txn / min</span>
            </div>
            <div class="metric" [class.metric--good]="data.successRate >= 0.99" [class.metric--warn]="data.successRate >= 0.95 && data.successRate < 0.99" [class.metric--bad]="data.successRate < 0.95">
              <span class="metric__value">{{ data.successRate * 100 | number: '1.1-1' }}%</span>
              <span class="metric__label">Success Rate</span>
            </div>
            <div class="metric" [class.metric--bad]="data.errorRate > 0.05" [class.metric--warn]="data.errorRate > 0.01 && data.errorRate <= 0.05">
              <span class="metric__value">{{ data.errorRate * 100 | number: '1.1-1' }}%</span>
              <span class="metric__label">Error Rate</span>
            </div>
            <div class="metric" [class.metric--warn]="data.latencyP95Ms > 2000" [class.metric--bad]="data.latencyP95Ms > 5000">
              <span class="metric__value">{{ data.latencyP95Ms | number: '1.0-0' }}ms</span>
              <span class="metric__label">Latency p95</span>
            </div>
            <div class="metric dlq-metric" [class.metric--warn]="data.dlqDepth > 0" [class.metric--bad]="data.dlqDepth > 10">
              <span class="metric__value">{{ data.dlqDepth | number }}</span>
              <span class="metric__label">DLQ Depth</span>
            </div>
          </div>
        }
        @if (!loading && !error && !data) {
          <div class="widget-empty">No data available</div>
        }
      </div>
    </p-card>
  `,
  styles: [
    `
      .widget-title {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        font-size: 1rem;
        font-weight: 600;
      }
      .health-wrapper {
        position: relative;
        min-height: 180px;
      }
      .metrics-grid {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 1rem;
        padding-top: 0.5rem;
      }
      .metric {
        display: flex;
        flex-direction: column;
        align-items: center;
        padding: 0.75rem;
        border-radius: 8px;
        background: var(--p-surface-50, #f8fafc);
        gap: 0.25rem;
      }
      .dlq-metric {
        grid-column: span 2;
      }
      .metric__value {
        font-size: 1.5rem;
        font-weight: 700;
        color: var(--p-text-color, #1e293b);
      }
      .metric__label {
        font-size: 0.75rem;
        color: var(--p-text-muted-color, #64748b);
        text-transform: uppercase;
        letter-spacing: 0.05em;
      }
      .metric--good .metric__value {
        color: #10B981;
      }
      .metric--warn .metric__value {
        color: #F59E0B;
      }
      .metric--bad .metric__value {
        color: #EF4444;
      }
      .widget-error,
      .widget-empty {
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 0.5rem;
        height: 180px;
        color: var(--p-text-muted-color, #64748b);
        font-size: 0.875rem;
      }
      .widget-error {
        color: #EF4444;
      }
    `,
  ],
})
export class IngestionHealthComponent {
  @Input() data: IngestionHealthData | null = null;
  @Input() loading = false;
  @Input() error: string | null = null;
}
