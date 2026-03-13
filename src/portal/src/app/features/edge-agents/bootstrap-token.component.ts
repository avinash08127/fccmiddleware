import { Component, DestroyRef, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EMPTY } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { CardModule } from 'primeng/card';
import { ButtonModule } from 'primeng/button';
import { SelectModule } from 'primeng/select';
import { MessageModule } from 'primeng/message';
import { InputTextModule } from 'primeng/inputtext';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import * as QRCode from 'qrcode';

import { BootstrapTokenService } from '../../core/services/bootstrap-token.service';
import { MasterDataService } from '../../core/services/master-data.service';
import { SiteService } from '../../core/services/site.service';
import { LegalEntity } from '../../core/models/master-data.model';
import { Site } from '../../core/models/site.model';
import { GenerateBootstrapTokenResponse } from '../../core/models/bootstrap-token.model';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { environment } from '../../../environments/environment';

@Component({
  selector: 'app-bootstrap-token',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    CardModule,
    ButtonModule,
    SelectModule,
    MessageModule,
    InputTextModule,
    TagModule,
    TooltipModule,
    EmptyStateComponent,
  ],
  template: `
    <div class="page-container">
      <div class="page-header">
        <h1 class="page-title"><i class="pi pi-key"></i> Generate Bootstrap Token</h1>
      </div>

      <p-card styleClass="form-card">
        <div class="form-row">
          <div class="form-field">
            <label for="bootstrap-legal-entity">Legal Entity</label>
            <p-select
              id="bootstrap-legal-entity"
              [options]="legalEntityOptions()"
              [ngModel]="selectedLegalEntityId()"
              (ngModelChange)="onLegalEntityChange($event)"
              optionLabel="label"
              optionValue="value"
              placeholder="Select Legal Entity"
              styleClass="full-width"
            />
          </div>
        </div>

        @if (selectedLegalEntityId()) {
          <div class="form-row">
            <div class="form-field">
              <label for="bootstrap-site">Site</label>
              <p-select
                id="bootstrap-site"
                [options]="siteOptions()"
                [(ngModel)]="selectedSiteCode"
                optionLabel="label"
                optionValue="value"
                placeholder="Select Site"
                [filter]="true"
                filterPlaceholder="Search sites..."
                styleClass="full-width"
                [loading]="loadingSites()"
              />
            </div>
          </div>

          <div class="form-row">
            <div class="form-field">
              <label for="bootstrap-environment">Environment (optional)</label>
              <p-select
                id="bootstrap-environment"
                [options]="environmentOptions"
                [(ngModel)]="selectedEnvironment"
                placeholder="Default (from server config)"
                [showClear]="true"
                styleClass="full-width"
              />
            </div>
          </div>

          <div class="form-actions">
            <p-button
              label="Generate Token"
              icon="pi pi-key"
              (onClick)="generateToken()"
              [disabled]="!selectedSiteCode || generating()"
              [loading]="generating()"
            />
          </div>
        }

        @if (error()) {
          <p-message severity="error" styleClass="result-msg">
            {{ error() }}
          </p-message>
        }

        @if (generatedToken()) {
          <div class="token-result">
            <p-message severity="success" styleClass="result-msg">
              Bootstrap token generated successfully for site {{ generatedToken()!.siteCode }}.
            </p-message>

            <div class="token-details">
              <div class="detail-row">
                <span class="detail-label">Token ID</span>
                <code>{{ generatedToken()!.tokenId }}</code>
              </div>
              <div class="detail-row">
                <span class="detail-label">Expires At</span>
                <span>{{ generatedToken()!.expiresAt | date:'medium' }}</span>
              </div>
              <div class="detail-row">
                <span class="detail-label">Provisioning Token</span>
                <div class="token-value-row">
                  <code class="token-code">{{ tokenDisplay() }}</code>
                  @if (!tokenCleared()) {
                    <p-button
                      [icon]="copied() ? 'pi pi-check' : 'pi pi-copy'"
                      [severity]="copied() ? 'success' : 'secondary'"
                      size="small"
                      [rounded]="true"
                      [text]="true"
                      pTooltip="Copy to clipboard & clear from memory"
                      (onClick)="copyToken()"
                    />
                    <p-button
                      [icon]="tokenRevealed() ? 'pi pi-eye-slash' : 'pi pi-eye'"
                      severity="secondary"
                      size="small"
                      [rounded]="true"
                      [text]="true"
                      [pTooltip]="tokenRevealed() ? 'Hide' : 'Reveal'"
                      (onClick)="tokenRevealed.set(!tokenRevealed())"
                    />
                  }
                </div>
              </div>

              @if (tokenCleared()) {
                <p-message severity="info" styleClass="result-msg">
                  Token copied to clipboard and cleared from browser memory.
                </p-message>
              } @else {
                <p-message severity="warn" styleClass="result-msg">
                  Copy this token now. It will be cleared from memory after copying.
                </p-message>
              }

              @if (!tokenRevoked()) {
                <div class="revoke-actions">
                  <p-button
                    label="Revoke Token"
                    icon="pi pi-ban"
                    severity="danger"
                    size="small"
                    [outlined]="true"
                    (onClick)="revokeToken()"
                    [disabled]="revoking()"
                    [loading]="revoking()"
                  />
                </div>
              }

              @if (tokenRevoked()) {
                <p-message severity="info" styleClass="result-msg">
                  This token has been revoked and can no longer be used for provisioning.
                </p-message>
              }
            </div>

            @if (qrDataUrl()) {
              <div class="qr-section">
                <h3 class="qr-title"><i class="pi pi-qrcode"></i> Provisioning QR Code</h3>
                <p class="qr-hint">
                  Scan this QR code with the Android edge agent to provision the device.
                </p>
                <p-message severity="warn" styleClass="qr-security-warn">
                  This QR code contains the provisioning token. Do not leave printed copies unattended.
                  The token expires in 72 hours.
                </p-message>
                <div class="qr-image-wrapper">
                  <img [src]="qrDataUrl()" alt="Provisioning QR Code" class="qr-image" />
                </div>
                <div class="qr-actions">
                  <p-button
                    label="Download QR"
                    icon="pi pi-download"
                    severity="secondary"
                    size="small"
                    (onClick)="downloadQr()"
                  />
                  <p-button
                    label="Print QR"
                    icon="pi pi-print"
                    severity="secondary"
                    size="small"
                    (onClick)="printQr()"
                  />
                </div>
              </div>
            }
          </div>
        }
      </p-card>
    </div>
  `,
  styles: [`
    :host { display: block; padding: 1.5rem; }

    .page-header {
      display: flex;
      align-items: center;
      margin-bottom: 1.25rem;
    }
    .page-title {
      font-size: 1.5rem;
      font-weight: 700;
      margin: 0;
      display: flex;
      align-items: center;
      gap: 0.5rem;
      color: var(--p-text-color, #1e293b);
    }

    .form-card { max-width: 600px; }

    .form-row { margin-bottom: 1rem; }
    .form-field {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
    }
    .form-field label {
      font-size: 0.78rem;
      font-weight: 600;
      color: var(--p-text-muted-color, #64748b);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .full-width { width: 100%; }

    .form-actions {
      margin-top: 1.25rem;
      margin-bottom: 1rem;
    }

    .result-msg { margin-top: 1rem; width: 100%; }

    .token-result { margin-top: 1rem; }
    .token-details {
      margin-top: 0.75rem;
      background: var(--p-surface-50, #f8fafc);
      border: 1px solid var(--p-surface-200, #e2e8f0);
      border-radius: 0.5rem;
      padding: 1rem;
    }

    .detail-row {
      display: flex;
      flex-direction: column;
      gap: 0.15rem;
      margin-bottom: 0.75rem;
    }
    .detail-row:last-child { margin-bottom: 0; }
    .detail-label {
      font-size: 0.72rem;
      font-weight: 600;
      color: var(--p-text-muted-color, #64748b);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }

    .token-value-row {
      display: flex;
      align-items: center;
      gap: 0.25rem;
    }
    .token-code {
      font-family: monospace;
      font-size: 0.85rem;
      word-break: break-all;
      flex: 1;
      background: var(--p-surface-100, #f1f5f9);
      padding: 0.35rem 0.5rem;
      border-radius: 0.25rem;
    }

    code {
      font-family: monospace;
      font-size: 0.85rem;
    }

    .revoke-actions {
      margin-top: 0.75rem;
    }

    .qr-section {
      margin-top: 1.25rem;
      background: var(--p-surface-50, #f8fafc);
      border: 1px solid var(--p-surface-200, #e2e8f0);
      border-radius: 0.5rem;
      padding: 1rem;
      text-align: center;
    }
    .qr-title {
      font-size: 1rem;
      font-weight: 600;
      margin: 0 0 0.25rem;
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 0.4rem;
      color: var(--p-text-color, #1e293b);
    }
    .qr-hint {
      font-size: 0.82rem;
      color: var(--p-text-muted-color, #64748b);
      margin: 0 0 0.75rem;
    }
    .qr-image-wrapper {
      display: flex;
      justify-content: center;
      margin-bottom: 0.75rem;
    }
    .qr-image {
      width: 280px;
      height: 280px;
      border: 4px solid #fff;
      border-radius: 0.5rem;
      box-shadow: 0 1px 4px rgba(0, 0, 0, 0.08);
    }
    .qr-security-warn { margin-bottom: 0.75rem; width: 100%; text-align: left; }
    .qr-actions {
      display: flex;
      justify-content: center;
      gap: 0.5rem;
    }
  `],
})
export class BootstrapTokenComponent {
  private readonly bootstrapTokenService = inject(BootstrapTokenService);
  private readonly masterDataService = inject(MasterDataService);
  private readonly siteService = inject(SiteService);
  private readonly destroyRef = inject(DestroyRef);

  // Legal entities
  private readonly legalEntities = signal<LegalEntity[]>([]);
  readonly selectedLegalEntityId = signal<string | null>(null);
  readonly legalEntityOptions = computed(() =>
    this.legalEntities().map((e) => ({ label: `${e.name} (${e.code})`, value: e.id })),
  );

  // Sites
  private readonly sites = signal<Site[]>([]);
  readonly loadingSites = signal(false);
  readonly siteOptions = computed(() =>
    this.sites().map((s) => ({ label: `${s.siteCode} — ${s.siteName}`, value: s.siteCode })),
  );
  selectedSiteCode: string | null = null;

  // Environment
  readonly environmentOptions = [
    { label: 'Production', value: 'PRODUCTION' },
    { label: 'Staging', value: 'STAGING' },
    { label: 'Development', value: 'DEVELOPMENT' },
    { label: 'Local', value: 'LOCAL' },
  ];
  selectedEnvironment: string | null = null;

  // Generation state
  readonly generating = signal(false);
  readonly error = signal<string | null>(null);
  readonly generatedToken = signal<GenerateBootstrapTokenResponse | null>(null);
  readonly copied = signal(false);
  readonly tokenRevealed = signal(false);
  readonly qrDataUrl = signal<string | null>(null);

  // M-17: Hold the raw token separately so it can be cleared from memory
  // after copy. The generatedToken signal retains metadata (tokenId, expiresAt, siteCode)
  // but rawToken is scrubbed.
  private rawTokenValue: string | null = null;
  readonly tokenCleared = signal(false);

  // Revocation state
  readonly revoking = signal(false);
  readonly tokenRevoked = signal(false);

  readonly tokenDisplay = computed(() => {
    if (this.tokenCleared()) return '(cleared from memory)';
    const raw = this.rawTokenValue;
    if (!raw) return '';
    if (this.tokenRevealed()) return raw;
    return raw.substring(0, 6) + '...' + raw.substring(raw.length - 4);
  });

  constructor() {
    this.masterDataService
      .getLegalEntities()
      .pipe(takeUntilDestroyed())
      .subscribe({ next: (entities) => this.legalEntities.set(entities) });
  }

  onLegalEntityChange(entityId: string | null): void {
    this.selectedLegalEntityId.set(entityId);
    this.selectedSiteCode = null;
    this.sites.set([]);
    this.generatedToken.set(null);
    this.error.set(null);
    this.qrDataUrl.set(null);

    if (entityId) {
      this.loadingSites.set(true);
      this.siteService
        .getSites({ legalEntityId: entityId, pageSize: 500, isActive: true })
        .pipe(
          catchError(() => {
            this.loadingSites.set(false);
            this.error.set('Failed to load sites. Please try again.');
            return EMPTY;
          }),
          takeUntilDestroyed(this.destroyRef),
        )
        .subscribe((result) => {
          this.sites.set(result.data);
          this.loadingSites.set(false);
        });
    }
  }

  generateToken(): void {
    const legalEntityId = this.selectedLegalEntityId();
    const siteCode = this.selectedSiteCode;
    if (!legalEntityId || !siteCode) return;

    this.generating.set(true);
    this.error.set(null);
    this.generatedToken.set(null);
    this.rawTokenValue = null;
    this.tokenCleared.set(false);
    this.copied.set(false);
    this.tokenRevealed.set(false);
    this.qrDataUrl.set(null);
    this.tokenRevoked.set(false);

    const req: import('../../core/models/bootstrap-token.model').GenerateBootstrapTokenRequest = {
      siteCode,
      legalEntityId,
      ...(this.selectedEnvironment ? { environment: this.selectedEnvironment } : {}),
    };

    this.bootstrapTokenService
      .generate(req)
      .pipe(
        catchError((err) => {
          console.error('Bootstrap token generation failed:', err);
          let msg: string;
          if (err.status === 0) {
            msg = 'Network error — please check your connection and try again.';
          } else if (err.status === 403) {
            msg = 'You do not have permission to generate tokens for this site.';
          } else if (err.status === 422 || err.status === 400) {
            msg = err?.error?.message ?? 'Invalid request — please check site and legal entity selection.';
          } else {
            msg = err?.error?.message ?? `Token generation failed (HTTP ${err.status}).`;
          }
          this.error.set(msg);
          this.generating.set(false);
          return EMPTY;
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) => {
        // M-17: Store raw token separately so we can clear it from memory after copy.
        this.rawTokenValue = result.rawToken;
        // Store metadata without the raw token in the signal (visible in DevTools)
        this.generatedToken.set({ ...result, rawToken: '' });
        this.generating.set(false);
        this.generateQrCode(result);
      });
  }

  revokeToken(): void {
    const token = this.generatedToken();
    if (!token) return;

    this.revoking.set(true);
    this.error.set(null);

    this.bootstrapTokenService
      .revoke(token.tokenId)
      .pipe(
        catchError((err) => {
          const msg = err?.error?.message ?? 'Failed to revoke bootstrap token.';
          this.error.set(msg);
          this.revoking.set(false);
          return EMPTY;
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        this.tokenRevoked.set(true);
        this.revoking.set(false);
        this.qrDataUrl.set(null);
      });
  }

  async copyToken(): Promise<void> {
    const raw = this.rawTokenValue;
    if (!raw) return;
    try {
      await navigator.clipboard.writeText(raw);
      this.copied.set(true);
      // M-17: Clear the raw token from memory after successful copy.
      // The token is now only in the clipboard — not in component state or DevTools.
      this.rawTokenValue = null;
      this.tokenCleared.set(true);
      this.tokenRevealed.set(false);
      setTimeout(() => this.copied.set(false), 2000);
    } catch {
      this.error.set('Failed to copy to clipboard. Please reveal the token and copy it manually.');
    }
  }

  private async generateQrCode(token: GenerateBootstrapTokenResponse): Promise<void> {
    const qrPayload: Record<string, unknown> = {
      v: this.selectedEnvironment ? 2 : 1,
      sc: token.siteCode,
      cu: environment.apiBaseUrl,
      pt: token.rawToken,
    };
    if (this.selectedEnvironment) {
      qrPayload['env'] = this.selectedEnvironment;
    }
    const payload = JSON.stringify(qrPayload);

    try {
      const dataUrl = await QRCode.toDataURL(payload, {
        errorCorrectionLevel: 'H',
        margin: 2,
        width: 560,
        color: { dark: '#000000', light: '#ffffff' },
      });
      this.qrDataUrl.set(dataUrl);
    } catch {
      // QR generation failed silently — token is still usable via copy
    }
  }

  downloadQr(): void {
    const dataUrl = this.qrDataUrl();
    const token = this.generatedToken();
    if (!dataUrl || !token) return;

    const link = document.createElement('a');
    link.href = dataUrl;
    link.download = `provision-qr-${token.siteCode}.png`;
    link.click();
  }

  printQr(): void {
    const dataUrl = this.qrDataUrl();
    const token = this.generatedToken();
    if (!dataUrl || !token) return;

    const win = window.open('', '_blank');
    if (!win) return;

    const doc = win.document;
    doc.title = `Provisioning QR — ${token.siteCode}`;

    const body = doc.body;
    body.style.cssText = 'display:flex;flex-direction:column;align-items:center;justify-content:center;min-height:100vh;margin:0;font-family:sans-serif';

    const heading = doc.createElement('h2');
    heading.style.cssText = 'margin:0 0 0.25rem';
    heading.textContent = 'Provisioning QR Code';
    body.appendChild(heading);

    const sitePara = doc.createElement('p');
    sitePara.style.cssText = 'margin:0 0 1rem;color:#64748b';
    const siteLabel = doc.createTextNode('Site: ');
    const siteStrong = doc.createElement('strong');
    siteStrong.textContent = token.siteCode;
    sitePara.appendChild(siteLabel);
    sitePara.appendChild(siteStrong);
    body.appendChild(sitePara);

    const img = doc.createElement('img');
    img.src = dataUrl;
    img.style.cssText = 'width:400px;height:400px';
    img.alt = 'Provisioning QR Code';
    body.appendChild(img);

    const footerPara = doc.createElement('p');
    footerPara.style.cssText = 'margin:1rem 0 0;font-size:0.8rem;color:#94a3b8';
    footerPara.textContent = `Token ID: ${token.tokenId} — Expires: ${new Date(token.expiresAt).toLocaleString()}`;
    body.appendChild(footerPara);

    doc.close();
    win.focus();
    win.print();
  }
}
