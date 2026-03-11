import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { CardModule } from 'primeng/card';
import { TagModule } from 'primeng/tag';
import { LoadingSpinnerComponent } from '../../../../shared/components/loading-spinner/loading-spinner.component';
import { DashboardAlert, AlertSeverity } from '../../dashboard.model';

@Component({
  selector: 'app-active-alerts',
  standalone: true,
  imports: [CommonModule, CardModule, TagModule, LoadingSpinnerComponent],
  template: `
    <p-card>
      <ng-template pTemplate="title">
        <div class="alerts-header">
          <span class="widget-title">
            <i class="pi pi-bell"></i> Active Alerts
          </span>
          @if (alerts.length > 0) {
            <p-tag [value]="alerts.length.toString()" severity="danger" [rounded]="true" />
          }
        </div>
      </ng-template>
      <div class="alerts-wrapper">
        <app-loading-spinner [visible]="loading" />
        @if (!loading && error) {
          <div class="widget-error">
            <i class="pi pi-exclamation-triangle"></i>
            <span>{{ error }}</span>
          </div>
        }
        @if (!loading && !error) {
          @if (alerts.length === 0) {
            <div class="no-alerts">
              <i class="pi pi-check-circle"></i>
              <span>No active alerts — all systems operational</span>
            </div>
          } @else {
            <div class="alerts-list">
              @for (alert of alerts; track alert.id) {
                <div class="alert-item" [class]="'alert-item--' + alert.severity">
                  <div class="alert-item__icon">
                    <i [class]="severityIcon(alert.severity)"></i>
                  </div>
                  <div class="alert-item__body">
                    <span class="alert-item__message">{{ alert.message }}</span>
                    @if (alert.siteCode) {
                      <span class="alert-item__site">{{ alert.siteCode }}</span>
                    }
                  </div>
                  <div class="alert-item__meta">
                    <p-tag
                      [value]="alert.type | titlecase"
                      [severity]="typeSeverity(alert.type)"
                      [rounded]="true"
                    />
                    <span class="alert-item__time">{{ alert.createdAt | date: 'shortTime' }}</span>
                  </div>
                </div>
              }
            </div>
          }
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
      .alerts-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        width: 100%;
      }
      .alerts-wrapper {
        position: relative;
        min-height: 80px;
      }
      .no-alerts {
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 0.5rem;
        padding: 1.5rem;
        color: #10B981;
        font-size: 0.9rem;
      }
      .alerts-list {
        display: flex;
        flex-direction: column;
        gap: 0.5rem;
      }
      .alert-item {
        display: flex;
        align-items: center;
        gap: 0.75rem;
        padding: 0.625rem 0.875rem;
        border-radius: 8px;
        border-left: 4px solid transparent;
      }
      .alert-item--critical {
        background: #FEE2E2;
        border-left-color: #EF4444;
      }
      .alert-item--warning {
        background: #FEF3C7;
        border-left-color: #F59E0B;
      }
      .alert-item--info {
        background: #EFF6FF;
        border-left-color: #3B82F6;
      }
      .alert-item__icon {
        font-size: 1.1rem;
        flex-shrink: 0;
      }
      .alert-item--critical .alert-item__icon {
        color: #EF4444;
      }
      .alert-item--warning .alert-item__icon {
        color: #F59E0B;
      }
      .alert-item--info .alert-item__icon {
        color: #3B82F6;
      }
      .alert-item__body {
        flex: 1;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 0.125rem;
      }
      .alert-item__message {
        font-size: 0.875rem;
        font-weight: 500;
      }
      .alert-item__site {
        font-size: 0.75rem;
        color: var(--p-text-muted-color, #64748b);
      }
      .alert-item__meta {
        display: flex;
        flex-direction: column;
        align-items: flex-end;
        gap: 0.25rem;
        flex-shrink: 0;
      }
      .alert-item__time {
        font-size: 0.75rem;
        color: var(--p-text-muted-color, #64748b);
      }
      .widget-error {
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 0.5rem;
        padding: 1.5rem;
        color: #EF4444;
        font-size: 0.875rem;
      }
    `,
  ],
})
export class ActiveAlertsComponent {
  @Input() alerts: DashboardAlert[] = [];
  @Input() loading = false;
  @Input() error: string | null = null;

  severityIcon(severity: AlertSeverity): string {
    switch (severity) {
      case 'critical':
        return 'pi pi-times-circle';
      case 'warning':
        return 'pi pi-exclamation-triangle';
      default:
        return 'pi pi-info-circle';
    }
  }

  typeSeverity(type: string): 'danger' | 'warn' | 'info' | 'secondary' {
    switch (type) {
      case 'connectivity':
        return 'danger';
      case 'dlq':
        return 'warn';
      case 'stale_data':
        return 'warn';
      case 'reconciliation':
        return 'info';
      default:
        return 'secondary';
    }
  }
}
