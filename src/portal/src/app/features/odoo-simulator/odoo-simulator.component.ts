import { CommonModule } from '@angular/common';
import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';

type ExecutionMode = 'proxy' | 'direct';
type PullTarget = 'cloud' | 'edge';
type EndpointTarget = 'cloud' | 'edge';

interface RequestResult {
  ok: boolean;
  statusLabel: string;
  method: 'GET' | 'POST';
  serviceUrl: string;
  transportUrl: string;
  durationMs: number | null;
  responsePreview: string;
  note?: string;
}

interface SimulatorSettings {
  edgeBaseUrl: string;
  cloudBaseUrl: string;
  edgeApiKey: string;
  cloudApiKey: string;
}

interface PreAuthFormModel {
  siteCode: string;
  odooOrderId: string;
  pumpNumber: number | null;
  nozzleNumber: number | null;
  amountMinorUnits: number | null;
  unitPrice: number | null;
  currencyCode: string;
  customerTaxId: string;
}

interface CloudPullFormModel {
  siteCode: string;
  pumpNumber: number | null;
  from: string;
  pageSize: number | null;
  cursor: string;
}

interface EdgePullFormModel {
  pumpNumber: number | null;
  since: string;
  limit: number | null;
  offset: number | null;
}

@Component({
  selector: 'app-odoo-simulator',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule],
  templateUrl: './odoo-simulator.component.html',
  styleUrl: './odoo-simulator.component.scss',
})
export class OdooSimulatorComponent {
  readonly executionModeOptions: ReadonlyArray<{ label: string; value: ExecutionMode }> = [
    { label: 'Proxy (local dev)', value: 'proxy' },
    { label: 'Direct URL', value: 'direct' },
  ];

  readonly pullTargetOptions: ReadonlyArray<{ label: string; value: PullTarget }> = [
    { label: 'Cloud Middleware', value: 'cloud' },
    { label: 'Edge Agent', value: 'edge' },
  ];

  executionMode: ExecutionMode = this.defaultExecutionMode();
  pullTarget: PullTarget = 'cloud';

  settings: SimulatorSettings = {
    edgeBaseUrl: 'http://127.0.0.1:8585',
    cloudBaseUrl: 'http://localhost:5070',
    edgeApiKey: '',
    cloudApiKey: '',
  };

  preAuth: PreAuthFormModel = this.createDefaultPreAuthForm();
  cloudPull: CloudPullFormModel = {
    siteCode: 'SIM-001',
    pumpNumber: null,
    from: '',
    pageSize: 25,
    cursor: '',
  };
  edgePull: EdgePullFormModel = {
    pumpNumber: null,
    since: '',
    limit: 25,
    offset: 0,
  };

  readonly preAuthLoading = signal(false);
  readonly pullLoading = signal(false);
  readonly preAuthResult = signal<RequestResult | null>(null);
  readonly pullResult = signal<RequestResult | null>(null);

  get modeHint(): string {
    if (this.executionMode === 'proxy') {
      return 'Proxy mode uses the Angular dev server to forward Edge calls to 127.0.0.1:8585 and Cloud calls to localhost:5070. Edit proxy.conf.json if your local targets differ.';
    }

    return 'Direct mode calls the base URLs from the browser. This can fail if the target API does not allow browser CORS.';
  }

  get servicePreAuthUrl(): string {
    return `${this.normalizeBaseUrl(this.settings.edgeBaseUrl)}/api/v1/preauth`;
  }

  get preAuthPayloadPreview(): string {
    return this.prettyPrintJson(this.buildPreAuthPayload());
  }

  get preAuthHeadersPreview(): string {
    return this.prettyPrintJson(this.buildHeaders('edge', true));
  }

  get preAuthCurlPreview(): string {
    return this.buildCurlCommand(
      'POST',
      this.servicePreAuthUrl,
      this.buildHeaders('edge', true),
      this.buildPreAuthPayload()
    );
  }

  get servicePullUrl(): string {
    return this.pullTarget === 'cloud'
      ? this.buildCloudPullServiceUrl()
      : this.buildEdgePullServiceUrl();
  }

  get pullHeadersPreview(): string {
    return this.prettyPrintJson(this.buildHeaders(this.pullTarget, false));
  }

  get pullParametersPreview(): string {
    return this.prettyPrintJson(
      this.pullTarget === 'cloud'
        ? this.buildCloudPullQueryObject()
        : this.buildEdgePullQueryObject()
    );
  }

  get pullCurlPreview(): string {
    return this.buildCurlCommand(
      'GET',
      this.servicePullUrl,
      this.buildHeaders(this.pullTarget, false)
    );
  }

  resetPreAuth(): void {
    this.preAuth = this.createDefaultPreAuthForm();
    this.preAuthResult.set(null);
  }

  async submitPreAuth(): Promise<void> {
    if (!this.isValidPreAuth()) {
      this.preAuthResult.set({
        ok: false,
        statusLabel: 'Validation',
        method: 'POST',
        serviceUrl: this.servicePreAuthUrl,
        transportUrl: this.resolveTransportUrl('edge', '/api/v1/preauth'),
        durationMs: null,
        responsePreview: 'siteCode, odooOrderId, pumpNumber, nozzleNumber, amountMinorUnits, unitPrice, and currencyCode are required.',
      });
      return;
    }

    this.preAuthLoading.set(true);
    this.preAuthResult.set(null);

    try {
      const payload = this.buildPreAuthPayload();
      const result = await this.executeRequest(
        'edge',
        'POST',
        '/api/v1/preauth',
        this.servicePreAuthUrl,
        this.buildHeaders('edge', true),
        payload
      );
      this.preAuthResult.set(result);
    } finally {
      this.preAuthLoading.set(false);
    }
  }

  async runPull(): Promise<void> {
    this.pullLoading.set(true);
    this.pullResult.set(null);

    const target = this.pullTarget;
    const pathWithQuery = target === 'cloud'
      ? this.buildCloudPullPath()
      : this.buildEdgePullPath();
    const serviceUrl = target === 'cloud'
      ? this.buildCloudPullServiceUrl()
      : this.buildEdgePullServiceUrl();

    try {
      const result = await this.executeRequest(
        target,
        'GET',
        pathWithQuery,
        serviceUrl,
        this.buildHeaders(target, false)
      );
      this.pullResult.set(result);
    } finally {
      this.pullLoading.set(false);
    }
  }

  private createDefaultPreAuthForm(): PreAuthFormModel {
    return {
      siteCode: 'SIM-001',
      odooOrderId: `POS/TEST/${new Date().toISOString().slice(11, 19).replaceAll(':', '')}`,
      pumpNumber: 1,
      nozzleNumber: 1,
      amountMinorUnits: 50000,
      unitPrice: 1100,
      currencyCode: 'NGN',
      customerTaxId: '',
    };
  }

  private defaultExecutionMode(): ExecutionMode {
    if (typeof window === 'undefined') {
      return 'direct';
    }

    return window.location.port === '4200' ? 'proxy' : 'direct';
  }

  private isValidPreAuth(): boolean {
    return Boolean(
      this.preAuth.siteCode.trim() &&
      this.preAuth.odooOrderId.trim() &&
      this.preAuth.currencyCode.trim() &&
      this.preAuth.pumpNumber &&
      this.preAuth.nozzleNumber &&
      this.preAuth.amountMinorUnits &&
      this.preAuth.unitPrice
    );
  }

  private buildPreAuthPayload(): Record<string, string | number> {
    const payload: Record<string, string | number> = {
      siteCode: this.preAuth.siteCode.trim(),
      odooOrderId: this.preAuth.odooOrderId.trim(),
      pumpNumber: Number(this.preAuth.pumpNumber),
      nozzleNumber: Number(this.preAuth.nozzleNumber),
      amountMinorUnits: Number(this.preAuth.amountMinorUnits),
      unitPrice: Number(this.preAuth.unitPrice),
      currencyCode: this.preAuth.currencyCode.trim().toUpperCase(),
    };

    const customerTaxId = this.preAuth.customerTaxId.trim();
    if (customerTaxId) {
      payload['customerTaxId'] = customerTaxId;
    }

    return payload;
  }

  private buildCloudPullPath(): string {
    return `/api/v1/transactions${this.buildQueryString(this.buildCloudPullQueryObject())}`;
  }

  private buildEdgePullPath(): string {
    return `/api/v1/transactions${this.buildQueryString(this.buildEdgePullQueryObject())}`;
  }

  private buildCloudPullServiceUrl(): string {
    return `${this.normalizeBaseUrl(this.settings.cloudBaseUrl)}${this.buildCloudPullPath()}`;
  }

  private buildEdgePullServiceUrl(): string {
    return `${this.normalizeBaseUrl(this.settings.edgeBaseUrl)}${this.buildEdgePullPath()}`;
  }

  private buildCloudPullQueryObject(): Record<string, string | number> {
    const query: Record<string, string | number> = {};

    if (this.cloudPull.siteCode.trim()) {
      query['siteCode'] = this.cloudPull.siteCode.trim();
    }

    if (this.cloudPull.pumpNumber) {
      query['pumpNumber'] = Number(this.cloudPull.pumpNumber);
    }

    if (this.cloudPull.from.trim()) {
      query['from'] = this.cloudPull.from.trim();
    }

    if (this.cloudPull.pageSize) {
      query['pageSize'] = Number(this.cloudPull.pageSize);
    }

    if (this.cloudPull.cursor.trim()) {
      query['cursor'] = this.cloudPull.cursor.trim();
    }

    return query;
  }

  private buildEdgePullQueryObject(): Record<string, string | number> {
    const query: Record<string, string | number> = {};

    if (this.edgePull.pumpNumber) {
      query['pumpNumber'] = Number(this.edgePull.pumpNumber);
    }

    if (this.edgePull.since.trim()) {
      query['since'] = this.edgePull.since.trim();
    }

    if (this.edgePull.limit) {
      query['limit'] = Number(this.edgePull.limit);
    }

    if (this.edgePull.offset !== null && this.edgePull.offset !== undefined) {
      query['offset'] = Number(this.edgePull.offset);
    }

    return query;
  }

  private buildHeaders(target: EndpointTarget, includeJsonContentType: boolean): Record<string, string> {
    const headers: Record<string, string> = {};

    if (includeJsonContentType) {
      headers['Content-Type'] = 'application/json';
    }

    const apiKey = target === 'edge'
      ? this.settings.edgeApiKey.trim()
      : this.settings.cloudApiKey.trim();
    if (apiKey) {
      headers['X-Api-Key'] = apiKey;
    }

    return headers;
  }

  private buildQueryString(query: Record<string, string | number>): string {
    const params = new URLSearchParams();
    for (const [key, value] of Object.entries(query)) {
      params.set(key, String(value));
    }

    const queryString = params.toString();
    return queryString ? `?${queryString}` : '';
  }

  private resolveTransportUrl(target: EndpointTarget, pathWithQuery: string): string {
    if (this.executionMode === 'proxy') {
      return `/simulator-api/${target}${pathWithQuery}`;
    }

    const baseUrl = target === 'edge'
      ? this.settings.edgeBaseUrl
      : this.settings.cloudBaseUrl;
    return `${this.normalizeBaseUrl(baseUrl)}${pathWithQuery}`;
  }

  private normalizeBaseUrl(url: string): string {
    return url.trim().replace(/\/+$/, '');
  }

  private async executeRequest(
    target: EndpointTarget,
    method: 'GET' | 'POST',
    pathWithQuery: string,
    serviceUrl: string,
    headers: Record<string, string>,
    body?: unknown
  ): Promise<RequestResult> {
    const transportUrl = this.resolveTransportUrl(target, pathWithQuery);
    const startedAt = this.nowMs();

    try {
      const response = await fetch(transportUrl, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
      });

      const rawText = await response.text();
      const durationMs = Math.round(this.nowMs() - startedAt);
      const note = this.buildResponseNote(target, response.status);

      return {
        ok: response.ok,
        statusLabel: `HTTP ${response.status} ${response.statusText}`.trim(),
        method,
        serviceUrl,
        transportUrl,
        durationMs,
        responsePreview: this.prettyPrintText(rawText),
        note,
      };
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unknown browser error';
      return {
        ok: false,
        statusLabel: 'Request failed',
        method,
        serviceUrl,
        transportUrl,
        durationMs: Math.round(this.nowMs() - startedAt),
        responsePreview: message,
        note: this.buildNetworkFailureNote(target),
      };
    }
  }

  private buildResponseNote(target: EndpointTarget, statusCode: number): string | undefined {
    if (statusCode === 401 && target === 'cloud' && !this.settings.cloudApiKey.trim()) {
      return 'Cloud pull normally requires an Odoo X-Api-Key header.';
    }

    if (statusCode === 401 && target === 'edge' && !this.settings.edgeApiKey.trim()) {
      return 'Edge requests from localhost do not need X-Api-Key, but LAN mode does.';
    }

    if (this.executionMode === 'proxy') {
      return 'Browser request went through the local Angular dev proxy.';
    }

    return undefined;
  }

  private buildNetworkFailureNote(target: EndpointTarget): string {
    if (this.executionMode === 'proxy') {
      return `Proxy mode expects \`ng serve\` to be running with proxy support and the ${target === 'edge' ? 'Edge Agent' : 'Cloud API'} reachable at the configured proxy target.`;
    }

    return 'Direct browser calls can fail on CORS. If that happens, switch to Proxy mode in local dev or run the cURL preview instead.';
  }

  private buildCurlCommand(
    method: 'GET' | 'POST',
    serviceUrl: string,
    headers: Record<string, string>,
    body?: unknown
  ): string {
    const parts = [`curl -i -X ${method}`, this.shellQuote(serviceUrl)];

    for (const [key, value] of Object.entries(headers)) {
      parts.push(`-H ${this.shellQuote(`${key}: ${value}`)}`);
    }

    if (body !== undefined) {
      parts.push(`--data-raw ${this.shellQuote(JSON.stringify(body))}`);
    }

    return parts.join(' ');
  }

  private shellQuote(value: string): string {
    return `'${value.replace(/'/g, `'\"'\"'`)}'`;
  }

  private prettyPrintJson(value: unknown): string {
    return JSON.stringify(value, null, 2);
  }

  private prettyPrintText(value: string): string {
    if (!value.trim()) {
      return '(empty response body)';
    }

    try {
      return JSON.stringify(JSON.parse(value), null, 2);
    } catch {
      return value;
    }
  }

  private nowMs(): number {
    return typeof performance !== 'undefined' ? performance.now() : Date.now();
  }
}
