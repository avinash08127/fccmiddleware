import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import {
  type CallbackHistoryRecord,
  type ScenarioDefinitionRecord,
  type ScenarioImportRecord,
  type ScenarioRunDetailRecord,
  type ScenarioRunSummaryRecord,
  type SiteDetail,
  type SiteListItem,
  LabApiService,
} from '../../core/services/lab-api.service';

@Component({
  selector: 'vl-scenarios',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <section class="hero">
      <div>
        <p class="eyebrow">VL-3 Scenarios And Callback Validation</p>
        <h2>Run deterministic walkthroughs and replay captured callbacks from one surface.</h2>
        <p class="copy">
          Scenario definitions, recent runs, callback history, and JSON import/export all stay
          available without leaving the lab UI.
        </p>
      </div>

      <div class="hero-actions">
        <button type="button" (click)="refreshAll()" [disabled]="busy() !== null">Refresh</button>
        <button type="button" class="secondary" (click)="exportDefinitions()" [disabled]="busy() !== null">
          Export JSON
        </button>
      </div>
    </section>

    <section *ngIf="error()" class="error-banner">{{ error() }}</section>

    <section class="workspace">
      <article class="panel">
        <header class="panel-header">
          <div>
            <h3>Scenario Library</h3>
            <p>{{ definitions().length }} definitions · {{ runs().length }} recent runs</p>
          </div>
          <label class="compact-input">
            <span>Replay seed</span>
            <input
              type="number"
              [ngModel]="replaySeed()"
              (ngModelChange)="setReplaySeed($event)"
            />
          </label>
        </header>

        <div class="stack" *ngIf="definitions().length; else emptyDefinitions">
          <button
            type="button"
            class="definition-row"
            *ngFor="let definition of definitions()"
            [class.definition-row--selected]="selectedScenarioId() === definition.id"
            (click)="selectScenario(definition)"
          >
            <div>
              <strong>{{ definition.name }}</strong>
              <p>{{ definition.scenarioKey }} · {{ definition.script.siteCode }}</p>
            </div>
            <div class="row-meta">
              <span class="pill">{{ definition.script.actions.length }} steps</span>
              <small>{{ definition.latestRun?.status || 'Not run yet' }}</small>
            </div>
          </button>
        </div>

        <button
          type="button"
          class="run-button"
          (click)="runSelected()"
          [disabled]="!selectedScenario() || busy() !== null"
        >
          {{ busy() === 'run' ? 'Running scenario…' : 'Run selected scenario' }}
        </button>
      </article>

      <article class="panel" *ngIf="selectedScenario() as scenario; else emptySelection">
        <header class="panel-header">
          <div>
            <h3>{{ scenario.name }}</h3>
            <p>{{ scenario.description }}</p>
          </div>
          <span class="pill">{{ scenario.replaySignature }}</span>
        </header>

        <div class="summary-grid">
          <div>
            <span>Site</span>
            <strong>{{ scenario.script.siteCode }}</strong>
          </div>
          <div>
            <span>Setup profile</span>
            <strong>{{ scenario.script.setup.profileKey || 'Current site profile' }}</strong>
          </div>
          <div>
            <span>Delivery</span>
            <strong>{{ scenario.script.setup.deliveryMode || 'Unchanged' }}</strong>
          </div>
          <div>
            <span>Pre-auth</span>
            <strong>{{ scenario.script.setup.preAuthMode || 'Unchanged' }}</strong>
          </div>
        </div>

        <div class="detail-columns">
          <section>
            <h4>Actions</h4>
            <article class="code-card" *ngFor="let action of scenario.script.actions; let index = index">
              <strong>{{ index + 1 }}. {{ action.name || action.kind }}</strong>
              <p>{{ action.kind }}<span *ngIf="action.action"> · {{ action.action }}</span></p>
              <pre>{{ formatJson(action) }}</pre>
            </article>
          </section>

          <section>
            <h4>Assertions</h4>
            <article class="code-card" *ngFor="let assertion of scenario.script.assertions; let index = index">
              <strong>{{ index + 1 }}. {{ assertion.name || assertion.kind }}</strong>
              <p>{{ assertion.kind }}</p>
              <pre>{{ formatJson(assertion) }}</pre>
            </article>
          </section>
        </div>

        <section class="run-results" *ngIf="selectedRun() as run">
          <div class="panel-header">
            <div>
              <h4>Latest selected run</h4>
              <p>{{ run.status }} · {{ run.correlationId }}</p>
            </div>
            <small>{{ formatDateTime(run.startedAtUtc) }}</small>
          </div>

          <div class="split">
            <div>
              <h5>Steps</h5>
              <article class="result-row" *ngFor="let step of run.steps">
                <div class="row-head">
                  <strong>{{ step.order }}. {{ step.name }}</strong>
                  <span class="pill" [class.pill--warning]="step.status !== 'Succeeded'">{{ step.status }}</span>
                </div>
                <p>{{ step.message }}</p>
                <small>{{ step.correlationId || 'n/a' }}</small>
                <pre>{{ formatJson(step.outputJson) }}</pre>
              </article>
            </div>

            <div>
              <h5>Assertions</h5>
              <article class="result-row" *ngFor="let assertion of run.assertions">
                <div class="row-head">
                  <strong>{{ assertion.order }}. {{ assertion.name }}</strong>
                  <span class="pill" [class.pill--warning]="!assertion.passed">
                    {{ assertion.passed ? 'Passed' : 'Failed' }}
                  </span>
                </div>
                <p>{{ assertion.message }}</p>
                <pre>{{ formatJson(assertion.outputJson) }}</pre>
              </article>
            </div>
          </div>
        </section>
      </article>

      <article class="panel side-panel">
        <section class="side-section">
          <header class="panel-header">
            <div>
              <h3>Recent Runs</h3>
              <p>Run detail stays queryable after refresh.</p>
            </div>
          </header>

          <button
            type="button"
            class="definition-row"
            *ngFor="let run of runs()"
            [class.definition-row--selected]="selectedRunId() === run.id"
            (click)="selectRun(run)"
          >
            <div>
              <strong>{{ run.scenarioName }}</strong>
              <p>{{ run.siteCode }} · {{ run.replaySeed }}</p>
            </div>
            <div class="row-meta">
              <span class="pill" [class.pill--warning]="run.status !== 'Completed'">{{ run.status }}</span>
              <small>{{ formatDateTime(run.startedAtUtc) }}</small>
            </div>
          </button>
        </section>

        <section class="side-section">
          <header class="panel-header">
            <div>
              <h3>Callback Replay</h3>
              <p>Inspect inbound callback history and replay captured traffic.</p>
            </div>
          </header>

          <label>
            <span>Site</span>
            <select [ngModel]="selectedSiteId()" (ngModelChange)="setSite($event)">
              <option value="">Choose a site</option>
              <option *ngFor="let site of sites()" [ngValue]="site.id">
                {{ site.siteCode }}
              </option>
            </select>
          </label>

          <label>
            <span>Callback target</span>
            <select [ngModel]="callbackTargetKey()" (ngModelChange)="setCallbackTarget($event)">
              <option value="">Choose a target</option>
              <option *ngFor="let target of callbackTargets()" [ngValue]="target.targetKey">
                {{ target.targetKey }} · {{ target.authMode }}
              </option>
            </select>
          </label>

          <button
            type="button"
            class="secondary"
            (click)="loadCallbackHistory()"
            [disabled]="!callbackTargetKey() || busy() !== null"
          >
            {{ busy() === 'callbacks' ? 'Loading history…' : 'Load callback history' }}
          </button>

          <section *ngIf="callbackError()" class="error-banner">{{ callbackError() }}</section>

          <article class="result-row" *ngFor="let capture of callbackHistory()">
            <div class="row-head">
              <strong>{{ capture.authOutcome }}</strong>
              <span class="pill" [class.pill--warning]="capture.responseStatusCode >= 400">
                {{ capture.responseStatusCode }}
              </span>
            </div>
            <p>{{ capture.correlationId }}<span *ngIf="capture.isReplay"> · replay</span></p>
            <small>{{ formatDateTime(capture.capturedAtUtc) }}</small>
            <pre>{{ formatJson(capture.requestPayloadJson) }}</pre>
            <button
              type="button"
              class="secondary"
              (click)="replayCapture(capture)"
              [disabled]="busy() !== null"
            >
              {{ busy() === capture.id ? 'Replaying…' : 'Replay capture' }}
            </button>
          </article>

          <p class="empty-state" *ngIf="callbackTargetKey() && callbackHistory().length === 0">
            No callback history loaded for the selected target yet.
          </p>
        </section>

        <section class="side-section">
          <header class="panel-header">
            <div>
              <h3>Import / Export</h3>
              <p>Paste exported JSON to replace or update the current library.</p>
            </div>
          </header>

          <label class="checkbox">
            <input
              type="checkbox"
              [ngModel]="replaceExisting()"
              (ngModelChange)="replaceExisting.set(!!$event)"
            />
            <span>Replace matching scenario keys on import</span>
          </label>

          <textarea
            [ngModel]="importJson()"
            (ngModelChange)="importJson.set($event)"
            rows="14"
            placeholder="Scenario export JSON appears here."
          ></textarea>

          <button type="button" (click)="importDefinitions()" [disabled]="busy() !== null || !importJson().trim()">
            {{ busy() === 'import' ? 'Importing…' : 'Import JSON' }}
          </button>
        </section>
      </article>
    </section>

    <ng-template #emptyDefinitions>
      <section class="empty-state">No scenario definitions are available.</section>
    </ng-template>

    <ng-template #emptySelection>
      <article class="panel empty-state">Select a scenario definition to inspect its script and latest run.</article>
    </ng-template>
  `,
  styles: [`
    :host {
      display: block;
      padding: 1.5rem;
      color: #e5eef5;
    }

    .hero,
    .panel {
      background: linear-gradient(180deg, rgba(14, 27, 36, 0.94), rgba(9, 19, 26, 0.94));
      border: 1px solid rgba(116, 145, 160, 0.24);
      border-radius: 1.25rem;
      box-shadow: 0 24px 60px rgba(0, 0, 0, 0.2);
    }

    .hero {
      padding: 1.5rem;
      display: grid;
      gap: 1rem;
      grid-template-columns: 1fr auto;
      margin-bottom: 1.25rem;
    }

    .hero-actions,
    .panel-header,
    .row-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 0.75rem;
    }

    .workspace {
      display: grid;
      grid-template-columns: minmax(18rem, 22rem) minmax(24rem, 1fr) minmax(20rem, 24rem);
      gap: 1rem;
      align-items: start;
    }

    .panel,
    .side-section {
      padding: 1rem;
    }

    .side-panel {
      display: grid;
      gap: 1rem;
    }

    .stack,
    .detail-columns,
    .split {
      display: grid;
      gap: 0.75rem;
    }

    .detail-columns,
    .split {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }

    .definition-row,
    button,
    select,
    input,
    textarea {
      border-radius: 0.9rem;
      border: 1px solid rgba(116, 145, 160, 0.2);
      background: rgba(9, 19, 26, 0.78);
      color: inherit;
    }

    .definition-row {
      width: 100%;
      padding: 0.85rem;
      text-align: left;
      display: flex;
      justify-content: space-between;
      gap: 0.75rem;
    }

    .definition-row--selected {
      border-color: rgba(77, 208, 225, 0.6);
      background: rgba(18, 43, 53, 0.9);
    }

    .summary-grid {
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      gap: 0.75rem;
      margin-bottom: 1rem;
    }

    .summary-grid div,
    .compact-input,
    label,
    .result-row,
    .code-card {
      display: grid;
      gap: 0.35rem;
    }

    button {
      padding: 0.75rem 1rem;
      cursor: pointer;
    }

    .secondary {
      background: rgba(18, 43, 53, 0.92);
    }

    .run-button {
      margin-top: 1rem;
      width: 100%;
    }

    input,
    select,
    textarea {
      padding: 0.7rem 0.8rem;
      width: 100%;
      box-sizing: border-box;
    }

    textarea,
    pre {
      font-family: 'IBM Plex Mono', monospace;
      font-size: 0.78rem;
    }

    pre {
      margin: 0;
      padding: 0.85rem;
      overflow: auto;
      background: rgba(4, 10, 14, 0.78);
      border-radius: 0.9rem;
      border: 1px solid rgba(116, 145, 160, 0.14);
    }

    .pill {
      padding: 0.2rem 0.55rem;
      border-radius: 999px;
      background: rgba(77, 208, 225, 0.16);
      font-size: 0.78rem;
    }

    .pill--warning {
      background: rgba(245, 158, 11, 0.18);
    }

    .eyebrow {
      margin: 0 0 0.35rem;
      text-transform: uppercase;
      letter-spacing: 0.14em;
      color: #7dd3cf;
      font-size: 0.72rem;
    }

    h2,
    h3,
    h4,
    h5,
    p {
      margin: 0;
    }

    .copy,
    small,
    .empty-state {
      color: #9fb4c1;
    }

    .error-banner {
      background: rgba(127, 29, 29, 0.55);
      border: 1px solid rgba(248, 113, 113, 0.32);
      color: #fecaca;
      padding: 0.85rem 1rem;
      border-radius: 1rem;
      margin-bottom: 1rem;
    }

    .checkbox {
      grid-template-columns: auto 1fr;
      align-items: center;
      gap: 0.6rem;
    }

    .checkbox input {
      width: auto;
    }

    @media (max-width: 1100px) {
      .workspace,
      .detail-columns,
      .split,
      .summary-grid,
      .hero {
        grid-template-columns: 1fr;
      }
    }
  `],
})
export class ScenariosComponent {
  private readonly api = inject(LabApiService);

  readonly definitions = signal<ScenarioDefinitionRecord[]>([]);
  readonly runs = signal<ScenarioRunSummaryRecord[]>([]);
  readonly selectedScenarioId = signal<string>('');
  readonly selectedRunId = signal<string>('');
  readonly selectedRun = signal<ScenarioRunDetailRecord | null>(null);
  readonly sites = signal<SiteListItem[]>([]);
  readonly siteDetail = signal<SiteDetail | null>(null);
  readonly selectedSiteId = signal<string>('');
  readonly callbackTargetKey = signal<string>('');
  readonly callbackHistory = signal<CallbackHistoryRecord[]>([]);
  readonly replaySeed = signal<number>(424242);
  readonly importJson = signal('');
  readonly replaceExisting = signal(true);
  readonly busy = signal<string | null>(null);
  readonly error = signal('');
  readonly callbackError = signal('');

  readonly selectedScenario = computed(() =>
    this.definitions().find((definition) => definition.id === this.selectedScenarioId()) ?? null,
  );

  readonly callbackTargets = computed(() => this.siteDetail()?.callbackTargets ?? []);

  constructor() {
    void this.refreshAll();
  }

  async refreshAll(): Promise<void> {
    this.error.set('');

    try {
      const [sites, scenarioLibrary] = await Promise.all([
        firstValueFrom(this.api.getSites()),
        firstValueFrom(this.api.getScenarioLibrary()),
      ]);

      this.sites.set(sites);
      this.definitions.set(scenarioLibrary.definitions);
      this.runs.set(scenarioLibrary.runs);

      const fallbackScenario = scenarioLibrary.definitions[0] ?? null;
      if (!this.selectedScenarioId() && fallbackScenario) {
        this.selectedScenarioId.set(fallbackScenario.id);
        this.replaySeed.set(fallbackScenario.deterministicSeed);
      }

      const fallbackSite = sites[0] ?? null;
      if (!this.selectedSiteId() && fallbackSite) {
        await this.setSite(fallbackSite.id);
      }
    } catch (error) {
      this.error.set(this.readError(error, 'Scenario library could not be loaded.'));
    }
  }

  selectScenario(definition: ScenarioDefinitionRecord): void {
    this.selectedScenarioId.set(definition.id);
    this.replaySeed.set(definition.deterministicSeed);
  }

  async runSelected(): Promise<void> {
    const scenario = this.selectedScenario();
    if (!scenario) {
      return;
    }

    this.busy.set('run');
    this.error.set('');

    try {
      const run = await firstValueFrom(
        this.api.runScenario({
          scenarioId: scenario.id,
          replaySeed: this.replaySeed(),
        }),
      );

      this.selectedRun.set(run);
      this.selectedRunId.set(run.id);
      await this.refreshAll();
    } catch (error) {
      this.error.set(this.readError(error, 'Scenario run failed.'));
    } finally {
      this.busy.set(null);
    }
  }

  async selectRun(run: ScenarioRunSummaryRecord): Promise<void> {
    this.busy.set(run.id);
    this.error.set('');

    try {
      const detail = await firstValueFrom(this.api.getScenarioRun(run.id));
      this.selectedRunId.set(run.id);
      this.selectedRun.set(detail);
      const matchingDefinition = this.definitions().find((definition) => definition.id === run.scenarioDefinitionId);
      if (matchingDefinition) {
        this.selectedScenarioId.set(matchingDefinition.id);
      }
    } catch (error) {
      this.error.set(this.readError(error, 'Scenario run detail could not be loaded.'));
    } finally {
      this.busy.set(null);
    }
  }

  async setSite(siteId: string): Promise<void> {
    this.selectedSiteId.set(siteId || '');
    this.callbackTargetKey.set('');
    this.callbackHistory.set([]);
    this.callbackError.set('');

    if (!siteId) {
      this.siteDetail.set(null);
      return;
    }

    this.busy.set('callbacks');
    try {
      const detail = await firstValueFrom(this.api.getSite(siteId));
      this.siteDetail.set(detail);
      const defaultTarget = detail.callbackTargets[0]?.targetKey ?? '';
      this.callbackTargetKey.set(defaultTarget);
    } catch (error) {
      this.callbackError.set(this.readError(error, 'Site callback targets could not be loaded.'));
    } finally {
      this.busy.set(null);
    }
  }

  setCallbackTarget(targetKey: string): void {
    this.callbackTargetKey.set(targetKey || '');
    this.callbackHistory.set([]);
    this.callbackError.set('');
  }

  async loadCallbackHistory(): Promise<void> {
    if (!this.callbackTargetKey()) {
      return;
    }

    this.busy.set('callbacks');
    this.callbackError.set('');

    try {
      const history = await firstValueFrom(this.api.getCallbackHistory(this.callbackTargetKey(), 50));
      this.callbackHistory.set(history);
    } catch (error) {
      this.callbackError.set(this.readError(error, 'Callback history could not be loaded.'));
    } finally {
      this.busy.set(null);
    }
  }

  async replayCapture(capture: CallbackHistoryRecord): Promise<void> {
    this.busy.set(capture.id);
    this.callbackError.set('');

    try {
      await firstValueFrom(this.api.replayCallback(capture.targetKey, capture.id));
      await this.loadCallbackHistory();
    } catch (error) {
      this.callbackError.set(this.readError(error, 'Callback replay failed.'));
    } finally {
      this.busy.set(null);
    }
  }

  async exportDefinitions(): Promise<void> {
    this.busy.set('export');
    this.error.set('');

    try {
      const definitions = await firstValueFrom(this.api.exportScenarios());
      this.importJson.set(JSON.stringify(definitions, null, 2));
    } catch (error) {
      this.error.set(this.readError(error, 'Scenario export failed.'));
    } finally {
      this.busy.set(null);
    }
  }

  async importDefinitions(): Promise<void> {
    this.busy.set('import');
    this.error.set('');

    try {
      const parsed = JSON.parse(this.importJson()) as ScenarioImportRecord[];
      await firstValueFrom(
        this.api.importScenarios({
          replaceExisting: this.replaceExisting(),
          definitions: parsed,
        }),
      );
      await this.refreshAll();
    } catch (error) {
      this.error.set(this.readError(error, 'Scenario import failed.'));
    } finally {
      this.busy.set(null);
    }
  }

  setReplaySeed(value: string | number): void {
    const parsed = Number(value);
    this.replaySeed.set(Number.isFinite(parsed) ? parsed : 424242);
  }

  formatJson(value: unknown): string {
    const source = typeof value === 'string' ? value : JSON.stringify(value);

    if (!source) {
      return '{}';
    }

    try {
      return JSON.stringify(JSON.parse(source), null, 2);
    } catch {
      return String(value);
    }
  }

  formatDateTime(value: string | null): string {
    if (!value) {
      return 'n/a';
    }

    return new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(value));
  }

  private readError(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) {
      if (typeof error.error === 'string' && error.error.trim()) {
        return error.error;
      }

      if (error.error?.message) {
        return String(error.error.message);
      }
    }

    return fallback;
  }
}
