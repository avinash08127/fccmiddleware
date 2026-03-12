import { CommonModule } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { catchError, merge, of, switchMap, timer } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DashboardSummary, LabApiService } from '../../core/services/lab-api.service';
import { LiveUpdatesService } from '../../core/services/live-updates.service';

@Component({
  selector: 'vl-dashboard',
  standalone: true,
  imports: [CommonModule],
  template: `
    <section class="hero">
      <div>
        <p class="eyebrow">VL-2.2 Operational Overview</p>
        <h2>Current environment health without manual refresh.</h2>
        <p class="copy">
          Dashboard data refreshes every 15 seconds and also reacts to SignalR lab events. Broken
          site compatibility stays visible instead of falling back to hidden defaults.
        </p>
      </div>

      <div class="hero-panel">
        <span class="meta">Last refresh</span>
        <strong>{{ dashboard()?.refreshedAtUtc ? formatDateTime(dashboard()!.refreshedAtUtc) : 'Loading…' }}</strong>
        <p>{{ dashboard()?.profileName ?? 'Seed profile pending' }}</p>
      </div>
    </section>

    <section *ngIf="error()" class="error-banner">
      {{ error() }}
    </section>

    <section class="kpi-grid" *ngIf="dashboard() as data">
      <article class="kpi-card">
        <span>Active transactions</span>
        <strong>{{ data.activeTransactions.total }}</strong>
        <small>Created, ready-for-delivery, and delivered transactions still in flight.</small>
      </article>
      <article class="kpi-card">
        <span>Auth failures (24h)</span>
        <strong>{{ data.authFailures.last24Hours }}</strong>
        <small>Inbound FCC or callback auth failures captured in structured logs.</small>
      </article>
      <article class="kpi-card">
        <span>Callback success rate</span>
        <strong>{{ data.callbackDelivery.successRatePercent | number: '1.0-1' }}%</strong>
        <small>
          {{ data.callbackDelivery.succeededLast24Hours }} success /
          {{ data.callbackDelivery.failedLast24Hours }} failed
        </small>
      </article>
      <article class="kpi-card warning">
        <span>Open callback retries</span>
        <strong>{{ data.callbackDelivery.pending }}</strong>
        <small>Pending or in-progress callback attempts still queued.</small>
      </article>
    </section>

    <section class="site-grid" *ngIf="dashboard() as data">
      <article class="site-card" *ngFor="let site of data.sites">
        <header>
          <div>
            <h3>{{ site.siteCode }}</h3>
            <p>{{ site.name }}</p>
          </div>
          <span class="status-chip" [class.invalid]="!site.compatibility.isValid">
            {{ site.compatibility.isValid ? 'Ready' : 'Needs attention' }}
          </span>
        </header>

        <dl>
          <div>
            <dt>Profile</dt>
            <dd>{{ site.activeProfile.name }}</dd>
          </div>
          <div>
            <dt>Delivery</dt>
            <dd>{{ site.deliveryMode }}</dd>
          </div>
          <div>
            <dt>Pre-auth</dt>
            <dd>{{ site.preAuthMode }}</dd>
          </div>
          <div>
            <dt>Forecourt</dt>
            <dd>{{ site.forecourt.activePumpCount }}/{{ site.forecourt.pumpCount }} pumps active</dd>
          </div>
        </dl>

        <div class="message-list" *ngIf="site.compatibility.messages.length > 0">
          <p
            *ngFor="let message of site.compatibility.messages.slice(0, 2)"
            [class.warning-copy]="message.severity !== 'Error'"
            [class.error-copy]="message.severity === 'Error'"
          >
            {{ message.path }}: {{ message.message }}
          </p>
        </div>
      </article>
    </section>

    <section class="panel-grid" *ngIf="dashboard() as data">
      <article class="panel">
        <header class="panel-header">
          <div>
            <h3>Active transactions</h3>
            <p>Most recent in-flight transactions across all sites.</p>
          </div>
          <span class="pill">{{ data.activeTransactions.total }}</span>
        </header>

        <div *ngIf="data.activeTransactions.items.length; else emptyTransactions" class="stack">
          <div class="row-card" *ngFor="let transaction of data.activeTransactions.items">
            <div>
              <strong>{{ transaction.siteCode }}</strong>
              <p>
                {{ transaction.externalTransactionId }} · {{ transaction.productCode }} · Pump
                {{ transaction.pumpNumber }}/Nozzle {{ transaction.nozzleNumber }}
              </p>
            </div>
            <div class="row-meta">
              <span>{{ transaction.status }}</span>
              <small>{{ formatDateTime(transaction.occurredAtUtc) }}</small>
            </div>
          </div>
        </div>
      </article>

      <article class="panel">
        <header class="panel-header">
          <div>
            <h3>Auth failures</h3>
            <p>Recent inbound auth issues from FCC-facing or callback endpoints.</p>
          </div>
          <span class="pill alert">{{ data.authFailures.last24Hours }}</span>
        </header>

        <div *ngIf="data.authFailures.items.length; else emptyAuthFailures" class="stack">
          <div class="row-card" *ngFor="let failure of data.authFailures.items">
            <div>
              <strong>{{ failure.siteCode ?? 'No site' }}</strong>
              <p>{{ failure.message }}</p>
            </div>
            <div class="row-meta">
              <span>{{ failure.eventType }}</span>
              <small>{{ formatDateTime(failure.occurredAtUtc) }}</small>
            </div>
          </div>
        </div>
      </article>
    </section>

    <section class="panel-grid" *ngIf="dashboard() as data">
      <article class="panel">
        <header class="panel-header">
          <div>
            <h3>Callback delivery</h3>
            <p>Success, failure, and retry state from callback attempts.</p>
          </div>
          <span class="pill">{{ data.callbackDelivery.successRatePercent | number: '1.0-1' }}%</span>
        </header>

        <div class="callback-summary">
          <div>
            <span>Succeeded</span>
            <strong>{{ data.callbackDelivery.succeededLast24Hours }}</strong>
          </div>
          <div>
            <span>Failed</span>
            <strong>{{ data.callbackDelivery.failedLast24Hours }}</strong>
          </div>
          <div>
            <span>Pending</span>
            <strong>{{ data.callbackDelivery.pending }}</strong>
          </div>
        </div>

        <div *ngIf="data.callbackDelivery.items.length" class="stack">
          <div class="row-card" *ngFor="let attempt of data.callbackDelivery.items">
            <div>
              <strong>{{ attempt.siteCode }} → {{ attempt.targetKey }}</strong>
              <p>
                Attempt {{ attempt.attemptNumber }} · HTTP {{ attempt.responseStatusCode }}
                <span *ngIf="attempt.errorMessage"> · {{ attempt.errorMessage }}</span>
              </p>
            </div>
            <div class="row-meta">
              <span [class.error-copy]="attempt.status === 'Failed'">{{ attempt.status }}</span>
              <small>{{ formatDateTime(attempt.attemptedAtUtc) }}</small>
            </div>
          </div>
        </div>
      </article>

      <article class="panel">
        <header class="panel-header">
          <div>
            <h3>Recent alerts</h3>
            <p>Warnings and errors kept visible for investigation.</p>
          </div>
          <span class="pill alert">{{ alertCount() }}</span>
        </header>

        <div *ngIf="data.recentAlerts.length; else emptyAlerts" class="stack">
          <div class="row-card" *ngFor="let alert of data.recentAlerts">
            <div>
              <strong>{{ alert.category }} · {{ alert.siteCode ?? 'Global' }}</strong>
              <p>{{ alert.message }}</p>
            </div>
            <div class="row-meta">
              <span [class.error-copy]="alert.severity === 'Error'">{{ alert.severity }}</span>
              <small>{{ formatDateTime(alert.occurredAtUtc) }}</small>
            </div>
          </div>
        </div>
      </article>
    </section>

    <ng-template #emptyTransactions>
      <p class="empty-state">No active transactions right now.</p>
    </ng-template>

    <ng-template #emptyAuthFailures>
      <p class="empty-state">No auth failures captured yet.</p>
    </ng-template>

    <ng-template #emptyAlerts>
      <p class="empty-state">No warning or error alerts in the last 24 hours.</p>
    </ng-template>
  `,
  styles: `
    :host {
      display: block;
    }

    .hero,
    .kpi-grid,
    .site-grid,
    .panel-grid {
      display: grid;
      gap: 1rem;
    }

    .hero {
      align-items: stretch;
      grid-template-columns: minmax(0, 1.8fr) minmax(280px, 1fr);
      margin-bottom: 1.5rem;
    }

    .hero-panel,
    .kpi-card,
    .site-card,
    .panel,
    .error-banner {
      background: var(--vl-panel-strong);
      border: 1px solid var(--vl-line);
      border-radius: 20px;
      box-shadow: var(--vl-shadow);
      padding: 1.25rem;
    }

    .hero-panel {
      background: linear-gradient(135deg, rgba(29, 122, 90, 0.16), rgba(255, 194, 123, 0.35));
      display: grid;
      gap: 0.35rem;
      place-content: center;
      text-align: center;
    }

    .eyebrow {
      color: var(--vl-accent);
      font-size: 0.8rem;
      letter-spacing: 0.16em;
      margin: 0 0 0.75rem;
      text-transform: uppercase;
    }

    h2,
    h3,
    p {
      margin: 0;
    }

    h2 {
      font-size: clamp(2rem, 4vw, 3.25rem);
      line-height: 0.95;
    }

    .copy,
    .meta,
    .row-card p,
    .panel-header p,
    .empty-state {
      color: var(--vl-muted);
    }

    .error-banner {
      color: #8b1e1e;
      margin-bottom: 1rem;
    }

    .kpi-grid {
      grid-template-columns: repeat(4, minmax(0, 1fr));
      margin-bottom: 1rem;
    }

    .kpi-card {
      display: grid;
      gap: 0.5rem;
    }

    .kpi-card strong {
      font-size: 2rem;
    }

    .kpi-card.warning {
      background: linear-gradient(180deg, rgba(207, 95, 45, 0.12), rgba(255, 250, 242, 0.92));
    }

    .site-grid {
      grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
      margin-bottom: 1rem;
    }

    .site-card {
      display: grid;
      gap: 1rem;
    }

    .site-card header,
    .panel-header,
    .row-card {
      display: flex;
      gap: 1rem;
      justify-content: space-between;
    }

    .status-chip,
    .pill {
      align-items: center;
      background: rgba(29, 122, 90, 0.12);
      border-radius: 999px;
      color: var(--vl-emerald);
      display: inline-flex;
      font-size: 0.82rem;
      font-weight: 600;
      padding: 0.3rem 0.7rem;
      white-space: nowrap;
    }

    .status-chip.invalid,
    .pill.alert {
      background: rgba(207, 95, 45, 0.14);
      color: var(--vl-accent);
    }

    dl {
      display: grid;
      gap: 0.75rem;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      margin: 0;
    }

    dt {
      color: var(--vl-muted);
      font-size: 0.85rem;
      margin-bottom: 0.15rem;
    }

    dd {
      margin: 0;
    }

    .message-list {
      display: grid;
      gap: 0.4rem;
    }

    .warning-copy {
      color: #7d5b1f;
    }

    .error-copy {
      color: #8b1e1e;
    }

    .panel-grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
      margin-bottom: 1rem;
    }

    .panel {
      display: grid;
      gap: 1rem;
    }

    .stack {
      display: grid;
      gap: 0.75rem;
    }

    .row-card {
      align-items: start;
      border: 1px solid var(--vl-line);
      border-radius: 16px;
      padding: 0.9rem 1rem;
    }

    .row-meta {
      display: grid;
      gap: 0.25rem;
      justify-items: end;
      text-align: right;
    }

    .callback-summary {
      display: grid;
      gap: 0.75rem;
      grid-template-columns: repeat(3, minmax(0, 1fr));
    }

    .callback-summary div {
      background: rgba(29, 122, 90, 0.06);
      border-radius: 16px;
      padding: 0.9rem 1rem;
    }

    .callback-summary span {
      color: var(--vl-muted);
      display: block;
      margin-bottom: 0.35rem;
    }

    .callback-summary strong {
      font-size: 1.35rem;
    }

    @media (max-width: 1100px) {
      .kpi-grid,
      .panel-grid,
      .hero {
        grid-template-columns: 1fr;
      }
    }

    @media (max-width: 720px) {
      dl,
      .callback-summary {
        grid-template-columns: 1fr;
      }

      .site-card header,
      .panel-header,
      .row-card {
        flex-direction: column;
      }

      .row-meta {
        justify-items: start;
        text-align: left;
      }
    }
  `,
})
export class DashboardComponent {
  readonly dashboard = signal<DashboardSummary | null>(null);
  readonly error = signal<string | null>(null);
  readonly alertCount = computed(() => this.dashboard()?.recentAlerts.length ?? 0);

  private readonly api = inject(LabApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly liveUpdates = inject(LiveUpdatesService);

  constructor() {
    merge(timer(0, 15000), this.liveUpdates.events$)
      .pipe(
        switchMap(() =>
          this.api.getDashboard().pipe(
            catchError(() => {
              this.error.set('Dashboard data could not be loaded. Check the API and SignalR connection.');
              return of(null);
            }),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(data => {
        if (data) {
          this.dashboard.set(data);
          this.error.set(null);
        }
      });
  }

  formatDateTime(value: string): string {
    return new Date(value).toLocaleString();
  }
}
