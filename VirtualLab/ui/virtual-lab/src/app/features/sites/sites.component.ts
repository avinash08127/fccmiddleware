import { CommonModule } from '@angular/common';
import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { LabApiService } from '../../core/services/lab-api.service';

@Component({
  selector: 'vl-sites',
  standalone: true,
  imports: [CommonModule],
  template: `
    <h2>Sites</h2>
    <p class="intro">
      Ten seeded sites define the shared benchmark shape for startup, dashboard, and FCC probe
      flows.
    </p>

    <section class="list">
      <article *ngFor="let site of sites()" class="site-card">
        <h3>{{ site.siteCode }}</h3>
        <p>{{ site.pumps }} pumps · {{ site.nozzles }} nozzles</p>
      </article>
    </section>
  `,
  styles: `
    .intro {
      color: var(--vl-muted);
      max-width: 60ch;
    }

    .list {
      display: grid;
      gap: 1rem;
      grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    }

    .site-card {
      background: var(--vl-panel-strong);
      border: 1px solid var(--vl-line);
      border-radius: 20px;
      padding: 1rem;
    }

    h3 {
      margin: 0 0 0.5rem;
    }

    p {
      color: var(--vl-muted);
      margin: 0;
    }
  `,
})
export class SitesComponent {
  private readonly api = inject(LabApiService);

  readonly sites = toSignal(this.api.getSites(), { initialValue: [] });
}
