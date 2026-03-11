import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { CardModule } from 'primeng/card';
import { LoadingSpinnerComponent } from '../../../../shared/components/loading-spinner/loading-spinner.component';
import { AgentStatusSummaryData } from '../../dashboard.model';
import { ConnectivityState } from '../../../../core/models';

@Component({
  selector: 'app-agent-status-summary',
  standalone: true,
  imports: [CommonModule, RouterLink, CardModule, LoadingSpinnerComponent],
  template: `
    <p-card styleClass="h-full">
      <ng-template pTemplate="title">
        <span class="widget-title">
          <i class="pi pi-server"></i> Edge Agent Status
        </span>
      </ng-template>
      <div class="agent-wrapper">
        <app-loading-spinner [visible]="loading" />
        @if (!loading && error) {
          <div class="widget-error">
            <i class="pi pi-exclamation-triangle"></i>
            <span>{{ error }}</span>
          </div>
        }
        @if (!loading && !error && data) {
          <div class="status-counts">
            <div class="status-pill status-pill--online">
              <i class="pi pi-circle-fill"></i>
              <span class="status-pill__count">{{ data.online }}</span>
              <span class="status-pill__label">Online</span>
            </div>
            <div class="status-pill status-pill--degraded">
              <i class="pi pi-circle-fill"></i>
              <span class="status-pill__count">{{ data.degraded }}</span>
              <span class="status-pill__label">Degraded</span>
            </div>
            <div class="status-pill status-pill--offline">
              <i class="pi pi-circle-fill"></i>
              <span class="status-pill__count">{{ data.offline }}</span>
              <span class="status-pill__label">Offline</span>
            </div>
          </div>
          @if (data.offlineAgents.length > 0) {
            <div class="offline-list">
              <p class="offline-list__heading">Offline Agents</p>
              @for (agent of data.offlineAgents.slice(0, 5); track agent.deviceId) {
                <div class="offline-agent">
                  <span class="offline-agent__site">{{ agent.siteCode }}</span>
                  <span class="offline-agent__state">{{ stateLabel(agent.connectivityState) }}</span>
                </div>
              }
              @if (data.offlineAgents.length > 5) {
                <a routerLink="/agents" class="view-all-link">
                  +{{ data.offlineAgents.length - 5 }} more — view all
                </a>
              }
            </div>
          }
          <div class="total-agents">
            <span>Total: {{ data.totalAgents }} agents</span>
            <a routerLink="/agents" class="view-all-link">View all</a>
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
      .agent-wrapper {
        position: relative;
        min-height: 180px;
      }
      .status-counts {
        display: flex;
        gap: 0.75rem;
        margin-bottom: 1rem;
      }
      .status-pill {
        flex: 1;
        display: flex;
        flex-direction: column;
        align-items: center;
        padding: 0.5rem;
        border-radius: 8px;
        gap: 0.125rem;
      }
      .status-pill--online {
        background: #D1FAE5;
        color: #065F46;
      }
      .status-pill--degraded {
        background: #FEF3C7;
        color: #92400E;
      }
      .status-pill--offline {
        background: #FEE2E2;
        color: #991B1B;
      }
      .status-pill__count {
        font-size: 1.5rem;
        font-weight: 700;
      }
      .status-pill__label {
        font-size: 0.7rem;
        text-transform: uppercase;
        letter-spacing: 0.05em;
      }
      .offline-list {
        border-top: 1px solid var(--p-surface-200, #e2e8f0);
        padding-top: 0.75rem;
        margin-bottom: 0.75rem;
      }
      .offline-list__heading {
        font-size: 0.75rem;
        font-weight: 600;
        text-transform: uppercase;
        color: var(--p-text-muted-color, #64748b);
        margin: 0 0 0.5rem;
      }
      .offline-agent {
        display: flex;
        justify-content: space-between;
        align-items: center;
        padding: 0.25rem 0;
        font-size: 0.875rem;
      }
      .offline-agent__site {
        font-weight: 600;
      }
      .offline-agent__state {
        font-size: 0.75rem;
        color: #EF4444;
      }
      .total-agents {
        display: flex;
        justify-content: space-between;
        align-items: center;
        font-size: 0.875rem;
        color: var(--p-text-muted-color, #64748b);
      }
      .view-all-link {
        font-size: 0.8rem;
        color: var(--p-primary-500, #3B82F6);
        text-decoration: none;
      }
      .view-all-link:hover {
        text-decoration: underline;
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
export class AgentStatusSummaryComponent {
  @Input() data: AgentStatusSummaryData | null = null;
  @Input() loading = false;
  @Input() error: string | null = null;

  stateLabel(state: ConnectivityState): string {
    switch (state) {
      case ConnectivityState.FULLY_OFFLINE:
        return 'Fully Offline';
      case ConnectivityState.INTERNET_DOWN:
        return 'Internet Down';
      case ConnectivityState.FCC_UNREACHABLE:
        return 'FCC Unreachable';
      default:
        return state;
    }
  }
}
