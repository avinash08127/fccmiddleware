import { CommonModule } from '@angular/common';
import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { LabApiService } from '../../core/services/lab-api.service';

@Component({
  selector: 'vl-dashboard',
  standalone: true,
  imports: [CommonModule],
  template: `
    <section class="hero">
      <div>
        <p class="eyebrow">Phase 0 Baseline</p>
        <h2>Seed, load, probe, repeat.</h2>
        <p class="copy">
          The dashboard scaffold consumes the benchmark-backed API so later work can track latency
          against a fixed seed profile instead of ad hoc demo data.
        </p>
      </div>
      <div class="hero-card">
        <p>Replay Signature</p>
        <strong>{{ latency()?.replaySignature ?? 'pending' }}</strong>
      </div>
    </section>

    <section class="grid">
      <article class="card">
        <span>Sites</span>
        <strong>{{ dashboard()?.sites ?? '...' }}</strong>
      </article>
      <article class="card">
        <span>Pumps</span>
        <strong>{{ dashboard()?.pumps ?? '...' }}</strong>
      </article>
      <article class="card">
        <span>Nozzles</span>
        <strong>{{ dashboard()?.nozzles ?? '...' }}</strong>
      </article>
      <article class="card">
        <span>Transactions</span>
        <strong>{{ dashboard()?.transactions ?? '...' }}</strong>
      </article>
    </section>

    <section class="metrics">
      <article class="metric-card">
        <h3>Probe Latencies</h3>
        <dl *ngIf="latency() as probe">
          <div>
            <dt>Dashboard p95</dt>
            <dd>{{ probe.measurements.dashboardQueryP95Ms | number: '1.0-2' }} ms</dd>
          </div>
          <div>
            <dt>Site load p95</dt>
            <dd>{{ probe.measurements.siteLoadP95Ms | number: '1.0-2' }} ms</dd>
          </div>
          <div>
            <dt>SignalR p95</dt>
            <dd>{{ probe.measurements.signalRBroadcastP95Ms | number: '1.0-2' }} ms</dd>
          </div>
          <div>
            <dt>FCC p95</dt>
            <dd>{{ probe.measurements.fccHealthP95Ms | number: '1.0-2' }} ms</dd>
          </div>
        </dl>
      </article>
      <article class="metric-card accent">
        <h3>Readiness Checklist</h3>
        <p>
          Local startup, proxy wiring, hosted smoke runs, and deterministic replay expectations are
          documented.
        </p>
      </article>
    </section>
  `,
  styles: `
    .hero,
    .grid,
    .metrics {
      display: grid;
      gap: 1rem;
    }

    .hero {
      align-items: stretch;
      grid-template-columns: 2fr minmax(220px, 1fr);
      margin-bottom: 1.5rem;
    }

    .hero-card,
    .card,
    .metric-card {
      background: var(--vl-panel-strong);
      border: 1px solid var(--vl-line);
      border-radius: 20px;
      padding: 1.25rem;
    }

    .hero-card {
      background: linear-gradient(135deg, rgba(29, 122, 90, 0.16), rgba(255, 194, 123, 0.4));
      display: grid;
      place-items: center;
      text-align: center;
    }

    .eyebrow {
      color: var(--vl-accent);
      font-size: 0.8rem;
      letter-spacing: 0.16em;
      margin: 0 0 0.75rem;
      text-transform: uppercase;
    }

    h2 {
      font-size: clamp(2rem, 4vw, 3.5rem);
      margin: 0;
    }

    .copy {
      color: var(--vl-muted);
      line-height: 1.7;
      max-width: 60ch;
    }

    .grid {
      grid-template-columns: repeat(4, minmax(0, 1fr));
      margin-bottom: 1.5rem;
    }

    .card strong {
      display: block;
      font-size: 2rem;
      margin-top: 0.5rem;
    }

    .metrics {
      grid-template-columns: 2fr 1fr;
    }

    dl {
      display: grid;
      gap: 0.75rem;
      margin: 0;
    }

    dt {
      color: var(--vl-muted);
    }

    dd {
      font-size: 1.2rem;
      margin: 0.2rem 0 0;
    }

    .accent {
      background: linear-gradient(180deg, rgba(207, 95, 45, 0.12), rgba(255, 250, 242, 0.9));
    }

    @media (max-width: 900px) {
      .hero,
      .metrics,
      .grid {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class DashboardComponent {
  private readonly api = inject(LabApiService);

  readonly dashboard = toSignal(this.api.getDashboard());
  readonly latency = toSignal(this.api.getLatency());
}
