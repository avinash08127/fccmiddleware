import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  FormArray,
  FormBuilder,
  FormGroup,
  ReactiveFormsModule,
} from '@angular/forms';
import { catchError, forkJoin, of } from 'rxjs';
import {
  CallbackTargetRecord,
  CallbackTargetUpsertRequest,
  DuplicateSiteRequest,
  FccProfileSummary,
  LabApiService,
  LabEnvironmentSummary,
  ManagementErrorResponse,
  ManagementValidationMessage,
  PreAuthFlowMode,
  SimulatedAuthMode,
  SiteDetail,
  SiteListItem,
  SiteSeedResult,
  SiteUpsertRequest,
  TransactionDeliveryMode,
} from '../../core/services/lab-api.service';

@Component({
  selector: 'vl-sites',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  template: `
    <section class="page-header">
      <div>
        <p class="eyebrow">VL-2.2 Site Management</p>
        <h2>Sites, callback targets, and explicit delivery settings.</h2>
        <p class="copy">
          Create, update, archive, duplicate, and seed site configurations from one screen. Push
          and hybrid sites require an explicit default callback target, and server-side validation
          stays visible in the editor.
        </p>
      </div>

      <div class="header-actions">
        <button type="button" class="secondary" (click)="prepareNewSite()">New site</button>
        <button type="button" (click)="saveSite()" [disabled]="saving()">
          {{ selectedSiteId() ? 'Save changes' : 'Create site' }}
        </button>
      </div>
    </section>

    <section *ngIf="actionMessage()" class="banner success">{{ actionMessage() }}</section>
    <section *ngIf="errorMessage()" class="banner error">{{ errorMessage() }}</section>

    <div class="workspace">
      <aside class="site-list">
        <article
          *ngFor="let site of sites()"
          class="site-tile"
          [class.active]="site.id === selectedSiteId()"
          (click)="selectSite(site.id)"
          (keydown.enter)="selectSite(site.id)"
          (keydown.space)="selectSite(site.id)"
          tabindex="0"
        >
          <div class="site-tile-header">
            <div>
              <strong>{{ site.siteCode }}</strong>
              <p>{{ site.name }}</p>
            </div>
            <span class="status-chip" [class.invalid]="!site.compatibility.isValid">
              {{ site.compatibility.isValid ? 'Ready' : 'Issues' }}
            </span>
          </div>
          <p>{{ site.deliveryMode }} · {{ site.preAuthMode }}</p>
          <small>{{ site.forecourt.pumpCount }} pumps · {{ site.forecourt.nozzleCount }} nozzles</small>
        </article>

        <p *ngIf="!sites().length" class="empty-state">No sites available. Create the first one.</p>
      </aside>

      <section class="editor">
        <div class="panel-toolbar">
          <div>
            <h3>{{ selectedSiteId() ? 'Edit site' : 'Create site' }}</h3>
            <p *ngIf="selectedSite(); else newSiteHint">
              {{ selectedSite()!.siteCode }} · {{ selectedSite()!.activeProfile.name }}
            </p>
            <ng-template #newSiteHint>
              <p>New drafts start with pull mode so callback configuration is explicit.</p>
            </ng-template>
          </div>

          <div class="toolbar-actions" *ngIf="selectedSiteId()">
            <button type="button" class="secondary" (click)="resetDraft()">Reset draft</button>
            <button type="button" class="secondary" (click)="seedSelectedSite()">Seed demo state</button>
            <button type="button" class="secondary" (click)="resetSelectedSite()">Reset state</button>
            <button type="button" class="danger" (click)="archiveSite()">Archive</button>
          </div>
        </div>

        <section *ngIf="validationMessages().length" class="validation-panel">
          <h4>Validation and compatibility</h4>
          <ul>
            <li *ngFor="let message of validationMessages()">
              <strong>{{ message.path }}</strong>: {{ message.message }}
            </li>
          </ul>
        </section>

        <section *ngIf="selectedSite()?.compatibility?.messages?.length" class="compatibility-panel">
          <h4>Current site compatibility snapshot</h4>
          <ul>
            <li *ngFor="let message of selectedSite()!.compatibility.messages">
              <strong>{{ message.severity }}</strong> · {{ message.path }} · {{ message.message }}
            </li>
          </ul>
        </section>

        <form class="editor-grid" [formGroup]="siteForm">
          <article class="panel">
            <h4>Identity</h4>
            <label>
              Site code
              <input formControlName="siteCode" />
              <small *ngIf="messageFor('siteCode')" class="field-error">{{ messageFor('siteCode') }}</small>
            </label>
            <label>
              Name
              <input formControlName="name" />
              <small *ngIf="messageFor('name')" class="field-error">{{ messageFor('name') }}</small>
            </label>
            <label>
              External reference
              <input formControlName="externalReference" />
            </label>
            <div class="split">
              <label>
                Time zone
                <input formControlName="timeZone" />
                <small *ngIf="messageFor('timeZone')" class="field-error">{{ messageFor('timeZone') }}</small>
              </label>
              <label>
                Currency
                <input formControlName="currencyCode" />
                <small *ngIf="messageFor('currencyCode')" class="field-error">{{ messageFor('currencyCode') }}</small>
              </label>
            </div>
            <label class="checkbox">
              <input type="checkbox" formControlName="isActive" />
              Site active
            </label>
            <div formGroupName="settings">
              <label class="checkbox">
                <input type="checkbox" formControlName="isTemplate" />
                Site template
              </label>
            </div>
          </article>

          <article class="panel">
            <h4>Profile and delivery</h4>
            <label>
              FCC profile
              <select formControlName="activeFccSimulatorProfileId">
                <option value="">Select a profile</option>
                <option *ngFor="let profile of profiles()" [value]="profile.id">
                  {{ profile.name }} · {{ profile.profileKey }}
                </option>
              </select>
              <small *ngIf="messageFor('activeFccSimulatorProfileId')" class="field-error">
                {{ messageFor('activeFccSimulatorProfileId') }}
              </small>
            </label>
            <div class="split">
              <label>
                Delivery mode
                <select formControlName="deliveryMode">
                  <option *ngFor="let mode of deliveryModes" [value]="mode">{{ mode }}</option>
                </select>
                <small *ngIf="messageFor('deliveryMode')" class="field-error">{{ messageFor('deliveryMode') }}</small>
              </label>
              <label>
                Pre-auth mode
                <select formControlName="preAuthMode">
                  <option *ngFor="let mode of preAuthModes" [value]="mode">{{ mode }}</option>
                </select>
                <small *ngIf="messageFor('preAuthMode')" class="field-error">{{ messageFor('preAuthMode') }}</small>
              </label>
            </div>

            <div formGroupName="settings" class="stack">
              <label>
                Default callback target
                <select formControlName="defaultCallbackTargetKey">
                  <option value="">Select a callback target</option>
                  <option
                    *ngFor="let callbackControl of callbackTargets.controls"
                    [value]="callbackControl.get('targetKey')?.value ?? ''"
                  >
                    {{ callbackControl.get('name')?.value || callbackControl.get('targetKey')?.value }}
                  </option>
                </select>
                <small *ngIf="messageFor('settings.defaultCallbackTargetKey')" class="field-error">
                  {{ messageFor('settings.defaultCallbackTargetKey') }}
                </small>
              </label>

              <label>
                Pull page size
                <input type="number" formControlName="pullPageSize" />
                <small *ngIf="messageFor('settings.pullPageSize')" class="field-error">
                  {{ messageFor('settings.pullPageSize') }}
                </small>
              </label>
            </div>
          </article>

          <article class="panel">
            <h4>Inbound auth override</h4>
            <label>
              Auth mode
              <select formControlName="inboundAuthMode">
                <option *ngFor="let mode of authModes" [value]="mode">{{ mode }}</option>
              </select>
            </label>

            <div class="stack" *ngIf="siteForm.controls.inboundAuthMode.value === 'ApiKey'">
              <label>
                API key header
                <input formControlName="apiKeyHeaderName" />
              </label>
              <label>
                API key value
                <input formControlName="apiKeyValue" />
              </label>
            </div>

            <div class="stack" *ngIf="siteForm.controls.inboundAuthMode.value === 'BasicAuth'">
              <label>
                Basic auth username
                <input formControlName="basicAuthUsername" />
              </label>
              <label>
                Basic auth password
                <input formControlName="basicAuthPassword" type="password" />
              </label>
            </div>

            <small *ngIf="messageFor('inboundAuthMode')" class="field-error">{{ messageFor('inboundAuthMode') }}</small>
          </article>

          <article class="panel" formGroupName="settings">
            <div formGroupName="fiscalization" class="stack">
              <h4>Fiscalization</h4>
              <label>
                Fiscalization mode
                <input formControlName="mode" />
                <small *ngIf="messageFor('settings.fiscalization.mode')" class="field-error">
                  {{ messageFor('settings.fiscalization.mode') }}
                </small>
              </label>
              <div class="split">
                <label class="checkbox">
                  <input type="checkbox" formControlName="requireCustomerTaxId" />
                  Require customer tax ID
                </label>
                <label class="checkbox">
                  <input type="checkbox" formControlName="fiscalReceiptRequired" />
                  Fiscal receipt required
                </label>
              </div>
              <label>
                Tax authority name
                <input formControlName="taxAuthorityName" />
              </label>
              <label>
                Tax authority endpoint
                <input formControlName="taxAuthorityEndpoint" />
              </label>
            </div>
          </article>

          <article class="panel span-2">
            <div class="section-header">
              <div>
                <h4>Callback targets</h4>
                <p>Configure push delivery destinations and auth. Nothing is implied silently.</p>
              </div>
              <button type="button" class="secondary" (click)="addCallbackTarget()">Add callback target</button>
            </div>

            <div class="callback-grid" *ngIf="callbackTargets.controls.length; else emptyCallbacks">
              <section
                class="callback-card"
                *ngFor="let callbackControl of callbackTargets.controls; let index = index"
                [formGroup]="asGroup(callbackControl)"
              >
                <div class="section-header compact">
                  <strong>Callback {{ index + 1 }}</strong>
                  <button type="button" class="danger subtle" (click)="removeCallbackTarget(index)">
                    Remove
                  </button>
                </div>

                <div class="split">
                  <label>
                    Target key
                    <input formControlName="targetKey" />
                    <small *ngIf="messageFor('callbackTargets[' + index + '].targetKey')" class="field-error">
                      {{ messageFor('callbackTargets[' + index + '].targetKey') }}
                    </small>
                  </label>
                  <label>
                    Name
                    <input formControlName="name" />
                    <small *ngIf="messageFor('callbackTargets[' + index + '].name')" class="field-error">
                      {{ messageFor('callbackTargets[' + index + '].name') }}
                    </small>
                  </label>
                </div>

                <label>
                  Callback URL
                  <input formControlName="callbackUrl" />
                  <small *ngIf="messageFor('callbackTargets[' + index + '].callbackUrl')" class="field-error">
                    {{ messageFor('callbackTargets[' + index + '].callbackUrl') }}
                  </small>
                </label>

                <div class="split">
                  <label>
                    Auth mode
                    <select formControlName="authMode">
                      <option *ngFor="let mode of authModes" [value]="mode">{{ mode }}</option>
                    </select>
                  </label>
                  <label class="checkbox align-end">
                    <input type="checkbox" formControlName="isActive" />
                    Callback active
                  </label>
                </div>

                <div class="stack" *ngIf="callbackControl.get('authMode')?.value === 'ApiKey'">
                  <label>
                    API key header
                    <input formControlName="apiKeyHeaderName" />
                  </label>
                  <label>
                    API key value
                    <input formControlName="apiKeyValue" />
                  </label>
                </div>

                <div class="stack" *ngIf="callbackControl.get('authMode')?.value === 'BasicAuth'">
                  <label>
                    Basic auth username
                    <input formControlName="basicAuthUsername" />
                  </label>
                  <label>
                    Basic auth password
                    <input type="password" formControlName="basicAuthPassword" />
                  </label>
                </div>

                <small *ngIf="messageFor('callbackTargets[' + index + '].authMode')" class="field-error">
                  {{ messageFor('callbackTargets[' + index + '].authMode') }}
                </small>
              </section>
            </div>

            <ng-template #emptyCallbacks>
              <p class="empty-state">No callback targets configured yet.</p>
            </ng-template>
          </article>

          <article class="panel span-2" *ngIf="selectedSite() as site">
            <div class="section-header">
              <div>
                <h4>Simulation and forecourt summary</h4>
                <p>Use seed/reset for quick verification. Forecourt editing remains visible here.</p>
              </div>
            </div>

            <div class="summary-grid">
              <div>
                <span>Pumps</span>
                <strong>{{ site.forecourt.pumpCount }}</strong>
              </div>
              <div>
                <span>Nozzles</span>
                <strong>{{ site.forecourt.nozzleCount }}</strong>
              </div>
              <div>
                <span>Active pumps</span>
                <strong>{{ site.forecourt.activePumpCount }}</strong>
              </div>
              <div>
                <span>Active nozzles</span>
                <strong>{{ site.forecourt.activeNozzleCount }}</strong>
              </div>
            </div>
          </article>

          <article class="panel span-2" *ngIf="selectedSiteId()">
            <div class="section-header">
              <div>
                <h4>Duplicate from template or live site</h4>
                <p>Clone the selected site into a new draftable site or template.</p>
              </div>
              <button type="button" (click)="duplicateSelectedSite()" [disabled]="saving()">
                Create duplicate
              </button>
            </div>

            <div class="duplicate-grid" [formGroup]="duplicateForm">
              <label>
                New site code
                <input formControlName="siteCode" />
              </label>
              <label>
                New name
                <input formControlName="name" />
              </label>
              <label>
                External reference
                <input formControlName="externalReference" />
              </label>
              <label>
                Profile override
                <select formControlName="activeFccSimulatorProfileId">
                  <option value="">Keep current profile</option>
                  <option *ngFor="let profile of profiles()" [value]="profile.id">
                    {{ profile.name }} · {{ profile.profileKey }}
                  </option>
                </select>
              </label>
              <label class="checkbox">
                <input type="checkbox" formControlName="copyForecourt" />
                Copy forecourt
              </label>
              <label class="checkbox">
                <input type="checkbox" formControlName="copyCallbackTargets" />
                Copy callback targets
              </label>
              <label class="checkbox">
                <input type="checkbox" formControlName="markAsTemplate" />
                Mark duplicate as template
              </label>
              <label class="checkbox">
                <input type="checkbox" formControlName="activate" />
                Activate duplicate
              </label>
            </div>
          </article>
        </form>
      </section>
    </div>
  `,
  styles: `
    :host {
      display: block;
    }

    h2,
    h3,
    h4,
    p {
      margin: 0;
    }

    .eyebrow {
      color: var(--vl-accent);
      font-size: 0.8rem;
      letter-spacing: 0.16em;
      margin-bottom: 0.75rem;
      text-transform: uppercase;
    }

    .copy,
    .site-tile p,
    .site-tile small,
    .banner,
    .empty-state,
    .section-header p {
      color: var(--vl-muted);
    }

    .page-header,
    .workspace,
    .editor-grid,
    .split,
    .callback-grid,
    .summary-grid,
    .duplicate-grid {
      display: grid;
      gap: 1rem;
    }

    .page-header {
      align-items: start;
      grid-template-columns: minmax(0, 1.8fr) auto;
      margin-bottom: 1rem;
    }

    .header-actions,
    .toolbar-actions,
    .section-header,
    .site-tile-header,
    .panel-toolbar {
      display: flex;
      gap: 0.75rem;
      justify-content: space-between;
    }

    .workspace {
      grid-template-columns: 320px minmax(0, 1fr);
    }

    .site-list,
    .editor,
    .panel,
    .site-tile,
    .banner,
    .validation-panel,
    .compatibility-panel {
      background: var(--vl-panel-strong);
      border: 1px solid var(--vl-line);
      border-radius: 20px;
      box-shadow: var(--vl-shadow);
    }

    .site-list,
    .editor {
      padding: 1rem;
    }

    .site-list {
      display: grid;
      gap: 0.75rem;
      align-content: start;
      max-height: calc(100vh - 8rem);
      overflow: auto;
    }

    .site-tile {
      cursor: pointer;
      padding: 1rem;
    }

    .site-tile.active {
      border-color: rgba(207, 95, 45, 0.4);
      transform: translateY(-1px);
    }

    .status-chip {
      align-items: center;
      background: rgba(29, 122, 90, 0.12);
      border-radius: 999px;
      color: var(--vl-emerald);
      display: inline-flex;
      font-size: 0.8rem;
      font-weight: 600;
      padding: 0.25rem 0.6rem;
    }

    .status-chip.invalid {
      background: rgba(207, 95, 45, 0.14);
      color: var(--vl-accent);
    }

    .editor {
      display: grid;
      gap: 1rem;
    }

    .panel,
    .validation-panel,
    .compatibility-panel {
      padding: 1rem;
    }

    .validation-panel,
    .compatibility-panel {
      margin-bottom: 1rem;
    }

    .validation-panel ul,
    .compatibility-panel ul {
      margin: 0.75rem 0 0;
      padding-left: 1rem;
    }

    .editor-grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }

    .span-2 {
      grid-column: span 2;
    }

    .panel {
      display: grid;
      gap: 0.9rem;
    }

    .stack {
      display: grid;
      gap: 0.75rem;
    }

    .split {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }

    label {
      color: var(--vl-muted);
      display: grid;
      gap: 0.35rem;
      font-size: 0.95rem;
    }

    input,
    select {
      background: rgba(255, 255, 255, 0.75);
      border: 1px solid var(--vl-line);
      border-radius: 12px;
      color: var(--vl-text);
      min-width: 0;
      padding: 0.75rem 0.9rem;
    }

    button {
      background: var(--vl-accent);
      border: none;
      border-radius: 999px;
      color: white;
      cursor: pointer;
      padding: 0.75rem 1rem;
    }

    button.secondary {
      background: rgba(207, 95, 45, 0.12);
      color: var(--vl-accent);
    }

    button.danger {
      background: #8b1e1e;
    }

    button.subtle {
      padding: 0.45rem 0.8rem;
    }

    button:disabled {
      cursor: not-allowed;
      opacity: 0.6;
    }

    .banner {
      margin-bottom: 1rem;
      padding: 1rem 1.25rem;
    }

    .banner.success {
      color: var(--vl-emerald);
    }

    .banner.error,
    .field-error {
      color: #8b1e1e;
    }

    .checkbox {
      align-items: center;
      display: flex;
      gap: 0.6rem;
    }

    .checkbox input {
      min-width: auto;
      width: auto;
    }

    .align-end {
      align-self: end;
      margin-top: 1.6rem;
    }

    .callback-grid {
      grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
    }

    .callback-card {
      border: 1px solid var(--vl-line);
      border-radius: 16px;
      display: grid;
      gap: 0.75rem;
      padding: 1rem;
    }

    .compact {
      align-items: center;
    }

    .summary-grid,
    .duplicate-grid {
      grid-template-columns: repeat(4, minmax(0, 1fr));
    }

    .summary-grid div {
      background: rgba(29, 122, 90, 0.06);
      border-radius: 16px;
      padding: 0.9rem 1rem;
    }

    .summary-grid span {
      color: var(--vl-muted);
      display: block;
      margin-bottom: 0.35rem;
    }

    .summary-grid strong {
      font-size: 1.35rem;
    }

    @media (max-width: 1200px) {
      .workspace,
      .page-header,
      .editor-grid,
      .summary-grid,
      .duplicate-grid {
        grid-template-columns: 1fr;
      }

      .span-2 {
        grid-column: span 1;
      }
    }

    @media (max-width: 720px) {
      .split,
      .section-header,
      .panel-toolbar,
      .header-actions,
      .toolbar-actions,
      .site-tile-header {
        flex-direction: column;
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class SitesComponent {
  private readonly api = inject(LabApiService);
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);

  readonly authModes: SimulatedAuthMode[] = ['None', 'ApiKey', 'BasicAuth'];
  readonly deliveryModes: TransactionDeliveryMode[] = ['Push', 'Pull', 'Hybrid'];
  readonly preAuthModes: PreAuthFlowMode[] = ['CreateOnly', 'CreateThenAuthorize'];

  readonly sites = signal<SiteListItem[]>([]);
  readonly profiles = signal<FccProfileSummary[]>([]);
  readonly environment = signal<LabEnvironmentSummary | null>(null);
  readonly selectedSite = signal<SiteDetail | null>(null);
  readonly selectedSiteId = signal<string | null>(null);
  readonly validationMessages = signal<ManagementValidationMessage[]>([]);
  readonly actionMessage = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);
  readonly saving = signal(false);

  readonly siteForm = this.fb.group({
    labEnvironmentId: [''],
    activeFccSimulatorProfileId: [''],
    siteCode: [''],
    name: [''],
    timeZone: ['UTC'],
    currencyCode: ['USD'],
    externalReference: [''],
    inboundAuthMode: ['None' as SimulatedAuthMode],
    apiKeyHeaderName: [''],
    apiKeyValue: [''],
    basicAuthUsername: [''],
    basicAuthPassword: [''],
    deliveryMode: ['Pull' as TransactionDeliveryMode],
    preAuthMode: ['CreateOnly' as PreAuthFlowMode],
    isActive: [true],
    settings: this.fb.group({
      isTemplate: [false],
      defaultCallbackTargetKey: [''],
      pullPageSize: [100],
      fiscalization: this.fb.group({
        mode: ['NONE'],
        requireCustomerTaxId: [false],
        fiscalReceiptRequired: [false],
        taxAuthorityName: [''],
        taxAuthorityEndpoint: [''],
      }),
    }),
    callbackTargets: this.fb.array<FormGroup>([]),
  });

  readonly duplicateForm = this.fb.group({
    siteCode: [''],
    name: [''],
    externalReference: [''],
    activeFccSimulatorProfileId: [''],
    copyForecourt: [true],
    copyCallbackTargets: [true],
    markAsTemplate: [false],
    activate: [true],
  });

  constructor() {
    forkJoin({
      environment: this.api.getLabEnvironment().pipe(catchError(() => of(null))),
      profiles: this.api.getProfiles().pipe(catchError(() => of([] as FccProfileSummary[]))),
      sites: this.api.getSites().pipe(catchError(() => of([] as SiteListItem[]))),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(({ environment, profiles, sites }) => {
        this.environment.set(environment);
        this.profiles.set(profiles);
        this.sites.set(sites);

        if (sites.length > 0) {
          this.selectSite(sites[0].id);
          return;
        }

        this.prepareNewSite();
      });
  }

  get callbackTargets(): FormArray<FormGroup> {
    return this.siteForm.controls.callbackTargets;
  }

  asGroup(control: unknown): FormGroup {
    return control as FormGroup;
  }

  messageFor(path: string): string | null {
    return this.validationMessages().find(message => message.path === path)?.message ?? null;
  }

  selectSite(id: string): void {
    this.errorMessage.set(null);
    this.actionMessage.set(null);

    this.api
      .getSite(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: site => {
          this.selectedSiteId.set(site.id);
          this.selectedSite.set(site);
          this.validationMessages.set(site.compatibility.messages ?? []);
          this.patchSiteForm(site);
        },
        error: () => {
          this.errorMessage.set('Site details could not be loaded.');
        },
      });
  }

  prepareNewSite(): void {
    const defaultProfileId = this.profiles()[0]?.id ?? '';
    const environmentId = this.environment()?.id ?? this.sites()[0]?.labEnvironmentId ?? '';

    this.selectedSite.set(null);
    this.selectedSiteId.set(null);
    this.validationMessages.set([]);
    this.errorMessage.set(null);
    this.actionMessage.set(null);
    this.siteForm.reset();
    this.siteForm.patchValue({
      labEnvironmentId: environmentId,
      activeFccSimulatorProfileId: defaultProfileId,
      siteCode: '',
      name: '',
      timeZone: 'UTC',
      currencyCode: 'USD',
      externalReference: '',
      inboundAuthMode: 'None',
      apiKeyHeaderName: '',
      apiKeyValue: '',
      basicAuthUsername: '',
      basicAuthPassword: '',
      deliveryMode: 'Pull',
      preAuthMode: 'CreateOnly',
      isActive: true,
      settings: {
        isTemplate: false,
        defaultCallbackTargetKey: '',
        pullPageSize: 100,
        fiscalization: {
          mode: 'NONE',
          requireCustomerTaxId: false,
          fiscalReceiptRequired: false,
          taxAuthorityName: '',
          taxAuthorityEndpoint: '',
        },
      },
    });

    this.callbackTargets.clear();
    this.resetDuplicateForm();
  }

  resetDraft(): void {
    if (this.selectedSite()) {
      this.patchSiteForm(this.selectedSite()!);
      this.validationMessages.set(this.selectedSite()!.compatibility.messages ?? []);
      return;
    }

    this.prepareNewSite();
  }

  addCallbackTarget(target?: CallbackTargetRecord): void {
    this.callbackTargets.push(this.createCallbackTargetGroup(target));
  }

  removeCallbackTarget(index: number): void {
    this.callbackTargets.removeAt(index);
  }

  saveSite(): void {
    const request = this.buildSiteRequest();
    if (!request) {
      return;
    }

    this.saving.set(true);
    this.errorMessage.set(null);
    this.actionMessage.set(null);

    const request$ = this.selectedSiteId()
      ? this.api.updateSite(this.selectedSiteId()!, request)
      : this.api.createSite(request);

    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: site => {
        this.saving.set(false);
        this.actionMessage.set(`Site '${site.siteCode}' saved.`);
        this.validationMessages.set(site.compatibility.messages ?? []);
        this.refreshSites(site.id);
      },
      error: error => {
        this.saving.set(false);
        this.handleManagementError(error, 'Site changes could not be saved.');
      },
    });
  }

  archiveSite(): void {
    if (!this.selectedSiteId()) {
      return;
    }

    this.saving.set(true);
    this.api
      .archiveSite(this.selectedSiteId()!)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: archived => {
          this.saving.set(false);
          this.actionMessage.set(`Site '${archived.siteCode}' archived.`);
          this.refreshSites(null);
        },
        error: error => {
          this.saving.set(false);
          this.handleManagementError(error, 'Site could not be archived.');
        },
      });
  }

  duplicateSelectedSite(): void {
    if (!this.selectedSiteId()) {
      return;
    }

    const request = this.duplicateForm.getRawValue() as DuplicateSiteRequest;
    this.saving.set(true);
    this.api
      .duplicateSite(this.selectedSiteId()!, request)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: duplicate => {
          this.saving.set(false);
          this.actionMessage.set(`Site '${duplicate.siteCode}' created from duplicate.`);
          this.refreshSites(duplicate.id);
        },
        error: error => {
          this.saving.set(false);
          this.handleManagementError(error, 'Site duplicate could not be created.');
        },
      });
  }

  seedSelectedSite(): void {
    if (!this.selectedSiteId()) {
      return;
    }

    this.saving.set(true);
    this.api
      .seedSite(this.selectedSiteId()!, {
        resetBeforeSeed: true,
        includeCompletedPreAuth: true,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => this.handleSeedResult(result, 'Demo state seeded'),
        error: () => {
          this.saving.set(false);
          this.errorMessage.set('Site seed failed.');
        },
      });
  }

  resetSelectedSite(): void {
    if (!this.selectedSiteId()) {
      return;
    }

    this.saving.set(true);
    this.api
      .resetSite(this.selectedSiteId()!)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => this.handleSeedResult(result, 'Simulation state reset'),
        error: () => {
          this.saving.set(false);
          this.errorMessage.set('Site reset failed.');
        },
      });
  }

  private handleSeedResult(result: SiteSeedResult, verb: string): void {
    this.saving.set(false);
    this.actionMessage.set(
      `${verb} for '${result.siteCode}'. Transactions created: ${result.transactionsCreated}, removed: ${result.transactionsRemoved}.`,
    );
    this.refreshSites(this.selectedSiteId());
  }

  private refreshSites(selectId: string | null): void {
    this.api
      .getSites()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: sites => {
          this.sites.set(sites);

          if (selectId) {
            const matching = sites.find(site => site.id === selectId);
            if (matching) {
              this.selectSite(selectId);
              return;
            }
          }

          if (sites.length > 0) {
            this.selectSite(sites[0].id);
            return;
          }

          this.prepareNewSite();
        },
        error: () => {
          this.errorMessage.set('Site list could not be refreshed.');
        },
      });
  }

  private patchSiteForm(site: SiteDetail): void {
    this.siteForm.patchValue({
      labEnvironmentId: site.labEnvironmentId,
      activeFccSimulatorProfileId: site.activeProfile.id,
      siteCode: site.siteCode,
      name: site.name,
      timeZone: site.timeZone,
      currencyCode: site.currencyCode,
      externalReference: site.externalReference,
      inboundAuthMode: site.inboundAuthMode,
      apiKeyHeaderName: site.apiKeyHeaderName,
      apiKeyValue: site.apiKeyValue,
      basicAuthUsername: site.basicAuthUsername,
      basicAuthPassword: site.basicAuthPassword,
      deliveryMode: site.deliveryMode,
      preAuthMode: site.preAuthMode,
      isActive: site.isActive,
      settings: {
        isTemplate: site.settings.isTemplate,
        defaultCallbackTargetKey: site.settings.defaultCallbackTargetKey,
        pullPageSize: site.settings.pullPageSize,
        fiscalization: {
          mode: site.settings.fiscalization.mode,
          requireCustomerTaxId: site.settings.fiscalization.requireCustomerTaxId,
          fiscalReceiptRequired: site.settings.fiscalization.fiscalReceiptRequired,
          taxAuthorityName: site.settings.fiscalization.taxAuthorityName,
          taxAuthorityEndpoint: site.settings.fiscalization.taxAuthorityEndpoint,
        },
      },
    });

    this.callbackTargets.clear();
    for (const target of site.callbackTargets) {
      this.callbackTargets.push(this.createCallbackTargetGroup(target));
    }

    this.duplicateForm.patchValue({
      siteCode: `${site.siteCode}-COPY`,
      name: `${site.name} Copy`,
      externalReference: `${site.externalReference || site.siteCode}-copy`,
      activeFccSimulatorProfileId: '',
      copyForecourt: true,
      copyCallbackTargets: true,
      markAsTemplate: site.settings.isTemplate,
      activate: site.isActive,
    });
  }

  private createCallbackTargetGroup(target?: CallbackTargetRecord): FormGroup {
    return this.fb.group({
      id: [target?.id ?? null],
      targetKey: [target?.targetKey ?? ''],
      name: [target?.name ?? ''],
      callbackUrl: [target?.callbackUrl ?? ''],
      authMode: [target?.authMode ?? ('None' as SimulatedAuthMode)],
      apiKeyHeaderName: [target?.apiKeyHeaderName ?? ''],
      apiKeyValue: [target?.apiKeyValue ?? ''],
      basicAuthUsername: [target?.basicAuthUsername ?? ''],
      basicAuthPassword: [target?.basicAuthPassword ?? ''],
      isActive: [target?.isActive ?? true],
    });
  }

  private buildSiteRequest(): SiteUpsertRequest | null {
    const environmentId =
      this.siteForm.controls.labEnvironmentId.value || this.environment()?.id || this.sites()[0]?.labEnvironmentId;

    if (!environmentId) {
      this.errorMessage.set('No lab environment is available for this site.');
      return null;
    }

    const callbackTargets = this.callbackTargets.getRawValue() as CallbackTargetUpsertRequest[];

    return {
      labEnvironmentId: environmentId,
      activeFccSimulatorProfileId: this.siteForm.controls.activeFccSimulatorProfileId.value ?? '',
      siteCode: this.siteForm.controls.siteCode.value ?? '',
      name: this.siteForm.controls.name.value ?? '',
      timeZone: this.siteForm.controls.timeZone.value ?? 'UTC',
      currencyCode: this.siteForm.controls.currencyCode.value ?? 'USD',
      externalReference: this.siteForm.controls.externalReference.value ?? '',
      inboundAuthMode: this.siteForm.controls.inboundAuthMode.value ?? 'None',
      apiKeyHeaderName: this.siteForm.controls.apiKeyHeaderName.value ?? '',
      apiKeyValue: this.siteForm.controls.apiKeyValue.value ?? '',
      basicAuthUsername: this.siteForm.controls.basicAuthUsername.value ?? '',
      basicAuthPassword: this.siteForm.controls.basicAuthPassword.value ?? '',
      deliveryMode: this.siteForm.controls.deliveryMode.value ?? 'Pull',
      preAuthMode: this.siteForm.controls.preAuthMode.value ?? 'CreateOnly',
      isActive: this.siteForm.controls.isActive.value ?? true,
      settings: {
        isTemplate: this.siteForm.controls.settings.controls.isTemplate.value ?? false,
        defaultCallbackTargetKey:
          this.siteForm.controls.settings.controls.defaultCallbackTargetKey.value ?? '',
        pullPageSize: Number(this.siteForm.controls.settings.controls.pullPageSize.value ?? 100),
        fiscalization: {
          mode:
            this.siteForm.controls.settings.controls.fiscalization.controls.mode.value ?? 'NONE',
          requireCustomerTaxId:
            this.siteForm.controls.settings.controls.fiscalization.controls.requireCustomerTaxId.value ??
            false,
          fiscalReceiptRequired:
            this.siteForm.controls.settings.controls.fiscalization.controls.fiscalReceiptRequired.value ??
            false,
          taxAuthorityName:
            this.siteForm.controls.settings.controls.fiscalization.controls.taxAuthorityName.value ?? '',
          taxAuthorityEndpoint:
            this.siteForm.controls.settings.controls.fiscalization.controls.taxAuthorityEndpoint.value ??
            '',
        },
      },
      callbackTargets,
    };
  }

  private resetDuplicateForm(): void {
    this.duplicateForm.reset({
      siteCode: '',
      name: '',
      externalReference: '',
      activeFccSimulatorProfileId: '',
      copyForecourt: true,
      copyCallbackTargets: true,
      markAsTemplate: false,
      activate: true,
    });
  }

  private handleManagementError(error: unknown, fallbackMessage: string): void {
    const response = (error as HttpErrorResponse).error as ManagementErrorResponse | undefined;
    this.validationMessages.set(response?.errors ?? []);
    this.errorMessage.set(response?.message ?? fallbackMessage);
  }
}
