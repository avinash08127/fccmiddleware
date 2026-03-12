import { CommonModule } from '@angular/common';
import { Component, OnInit, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { runtimeConfig } from '../config/runtime-config';
import { LiveUpdatesService } from '../services/live-updates.service';

interface NavItem {
  label: string;
  path: string;
}

@Component({
  selector: 'vl-app-shell',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="shell">
      <aside class="sidebar">
        <div>
          <p class="eyebrow">FCC Simulator + Pump Lab</p>
          <h1>Virtual Lab</h1>
          <p class="lede">
            Baseline workspace for seeded sites, FCC profiles, live forecourt controls, and
            deterministic scenario replay.
          </p>
        </div>

        <nav class="nav">
          <a
            *ngFor="let item of navItems"
            [routerLink]="item.path"
            routerLinkActive="active"
            class="nav-link"
          >
            {{ item.label }}
          </a>
        </nav>

        <section class="status-panel">
          <span class="pill">{{ environmentName }}</span>
          <span class="pill">{{ connectionState() }}</span>
          <p>Proxy-ready local development and Azure placeholders are configured from Phase 0.</p>
        </section>
      </aside>

      <main class="content">
        <router-outlet></router-outlet>
      </main>
    </div>
  `,
  styles: `
    .shell {
      display: grid;
      gap: 1.5rem;
      grid-template-columns: minmax(260px, 320px) minmax(0, 1fr);
      min-height: 100vh;
      padding: 1.5rem;
    }

    .sidebar,
    .content {
      backdrop-filter: blur(16px);
      background: var(--vl-panel);
      border: 1px solid var(--vl-line);
      border-radius: var(--vl-radius);
      box-shadow: var(--vl-shadow);
    }

    .sidebar {
      display: flex;
      flex-direction: column;
      gap: 1.5rem;
      padding: 1.5rem;
    }

    .content {
      padding: 1.5rem;
    }

    .eyebrow {
      color: var(--vl-accent);
      font-size: 0.78rem;
      letter-spacing: 0.18em;
      margin: 0 0 0.75rem;
      text-transform: uppercase;
    }

    h1 {
      font-size: clamp(2rem, 3vw, 3rem);
      line-height: 0.95;
      margin: 0;
    }

    .lede {
      color: var(--vl-muted);
      line-height: 1.6;
      margin: 1rem 0 0;
    }

    .nav {
      display: grid;
      gap: 0.5rem;
    }

    .nav-link {
      border: 1px solid transparent;
      border-radius: 999px;
      color: var(--vl-text);
      padding: 0.7rem 1rem;
      text-decoration: none;
      transition:
        background-color 160ms ease,
        transform 160ms ease;
    }

    .nav-link:hover,
    .nav-link.active {
      background: rgba(207, 95, 45, 0.12);
      border-color: rgba(207, 95, 45, 0.2);
      transform: translateX(4px);
    }

    .status-panel {
      background: var(--vl-panel-strong);
      border: 1px solid var(--vl-line);
      border-radius: 20px;
      margin-top: auto;
      padding: 1rem;
    }

    .pill {
      background: rgba(29, 122, 90, 0.12);
      border-radius: 999px;
      color: var(--vl-emerald);
      display: inline-flex;
      font-size: 0.8rem;
      margin-right: 0.5rem;
      padding: 0.3rem 0.65rem;
      text-transform: uppercase;
    }

    .status-panel p {
      color: var(--vl-muted);
      margin: 0.75rem 0 0;
    }

    @media (max-width: 900px) {
      .shell {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class AppShellComponent implements OnInit {
  readonly connectionState = inject(LiveUpdatesService).connectionState;
  readonly environmentName = runtimeConfig.environmentName;
  readonly navItems: NavItem[] = [
    { label: 'Dashboard', path: '/dashboard' },
    { label: 'Sites', path: '/sites' },
    { label: 'FCC Profiles', path: '/fcc-profiles' },
    { label: 'Forecourt Designer', path: '/forecourt-designer' },
    { label: 'Live Pump Console', path: '/live-console' },
    { label: 'Pre-Auth Console', path: '/preauth-console' },
    { label: 'Transactions', path: '/transactions' },
    { label: 'Logs', path: '/logs' },
    { label: 'Scenarios', path: '/scenarios' },
    { label: 'Settings', path: '/settings' },
  ];

  private readonly liveUpdates = inject(LiveUpdatesService);

  ngOnInit(): void {
    void this.liveUpdates.start();
  }
}
