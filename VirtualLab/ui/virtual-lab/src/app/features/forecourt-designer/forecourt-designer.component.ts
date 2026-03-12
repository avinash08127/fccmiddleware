import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  OnInit,
  ViewChild,
  inject,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';
import {
  ForecourtNozzleUpsertRequest,
  LabApiService,
  ManagementErrorResponse,
  ManagementValidationMessage,
  NozzleState,
  ProductView,
  SaveForecourtRequest,
  SiteForecourtView,
  SiteListItem,
} from '../../core/services/lab-api.service';

interface DesignerNozzle {
  id: string | null;
  localId: string;
  productId: string;
  productCode: string;
  productName: string;
  nozzleNumber: number;
  fccNozzleNumber: number;
  label: string;
  state: NozzleState;
  isActive: boolean;
}

interface DesignerPump {
  id: string | null;
  localId: string;
  pumpNumber: number;
  fccPumpNumber: number;
  layoutX: number;
  layoutY: number;
  label: string;
  isActive: boolean;
  nozzles: DesignerNozzle[];
}

interface DesignerForecourt {
  siteId: string;
  siteCode: string;
  siteName: string;
  pumps: DesignerPump[];
}

interface DragState {
  pumpLocalId: string;
  pointerId: number;
  originX: number;
  originY: number;
  startClientX: number;
  startClientY: number;
}

@Component({
  selector: 'vl-forecourt-designer',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './forecourt-designer.component.html',
  styleUrl: './forecourt-designer.component.scss',
})
export class ForecourtDesignerComponent implements OnInit {
  readonly canvasWidth = 1600;
  readonly canvasHeight = 900;
  readonly pumpWidth = 186;
  readonly pumpHeaderHeight = 90;
  readonly nozzleRowHeight = 36;
  readonly nozzleGap = 8;

  sites: SiteListItem[] = [];
  products: ProductView[] = [];
  forecourt: DesignerForecourt | null = null;
  selectedSiteId: string | null = null;
  selectedPumpLocalId: string | null = null;
  selectedNozzleLocalId: string | null = null;
  validationMessages: ManagementValidationMessage[] = [];
  actionMessage = '';
  errorMessage = '';
  loadingCatalog = false;
  loadingForecourt = false;
  saving = false;
  dirty = false;
  bulkPumpCount = 2;
  bulkNozzlesPerPump = 3;

  @ViewChild('canvas', { static: false }) canvasRef?: ElementRef<SVGSVGElement>;

  private readonly destroyRef = inject(DestroyRef);
  private readonly labApi = inject(LabApiService);
  private dragState: DragState | null = null;

  ngOnInit(): void {
    this.loadCatalog();
  }

  get selectedSite(): SiteListItem | null {
    return this.sites.find((site) => site.id === this.selectedSiteId) ?? null;
  }

  get selectedPump(): DesignerPump | null {
    return this.forecourt?.pumps.find((pump) => pump.localId === this.selectedPumpLocalId) ?? null;
  }

  get selectedNozzle(): DesignerNozzle | null {
    return (
      this.selectedPump?.nozzles.find((nozzle) => nozzle.localId === this.selectedNozzleLocalId) ??
      null
    );
  }

  get activeProductOptions(): ProductView[] {
    return this.products.filter((product) => product.isActive);
  }

  get totalNozzles(): number {
    return this.forecourt?.pumps.reduce((count, pump) => count + pump.nozzles.length, 0) ?? 0;
  }

  selectSite(siteId: string): void {
    if (siteId === this.selectedSiteId) {
      return;
    }

    if (!this.confirmDiscardChanges()) {
      return;
    }

    this.loadForecourt(siteId);
  }

  reloadForecourt(): void {
    if (!this.selectedSiteId || !this.confirmDiscardChanges()) {
      return;
    }

    this.loadForecourt(this.selectedSiteId);
  }

  addPump(): void {
    if (!this.forecourt) {
      return;
    }

    const nozzleCount = this.clampInteger(this.bulkNozzlesPerPump, 0, 8);
    if (!this.canProvisionNozzles(nozzleCount)) {
      return;
    }

    const nextPumpNumber = this.nextPumpNumber();
    const nextFccPumpNumber = this.nextFccPumpNumber();
    const coordinates = this.defaultPumpPosition(this.forecourt.pumps.length);
    const pump = this.createPumpDraft(
      nextPumpNumber,
      nextFccPumpNumber,
      nozzleCount,
      coordinates.x,
      coordinates.y,
    );

    this.forecourt.pumps.push(pump);
    this.sortForecourt();
    this.selectPump(pump.localId);
    this.markDirty('Added a pump to the draft forecourt.');
  }

  bulkCreatePumps(): void {
    if (!this.forecourt) {
      return;
    }

    const pumpCount = this.clampInteger(this.bulkPumpCount, 1, 24);
    const nozzleCount = this.clampInteger(this.bulkNozzlesPerPump, 0, 8);
    if (!this.canProvisionNozzles(nozzleCount)) {
      return;
    }

    let nextPumpNumber = this.nextPumpNumber();
    let nextFccPumpNumber = this.nextFccPumpNumber();

    for (let index = 0; index < pumpCount; index += 1) {
      const coordinates = this.defaultPumpPosition(this.forecourt.pumps.length);
      this.forecourt.pumps.push(
        this.createPumpDraft(
          nextPumpNumber,
          nextFccPumpNumber,
          nozzleCount,
          coordinates.x,
          coordinates.y,
        ),
      );
      nextPumpNumber += 1;
      nextFccPumpNumber += 1;
    }

    this.sortForecourt();
    this.selectPump(this.forecourt.pumps[this.forecourt.pumps.length - 1]?.localId ?? null);
    this.markDirty(`Added ${pumpCount} pumps to the draft forecourt.`);
  }

  cloneSelectedPump(): void {
    const source = this.selectedPump;
    if (!source || !this.forecourt) {
      return;
    }

    const pumpNumber = this.nextPumpNumber();
    const fccPumpNumber = this.nextFccPumpNumber();
    const clonedPump: DesignerPump = {
      id: null,
      localId: createLocalId('pump'),
      pumpNumber,
      fccPumpNumber,
      layoutX: this.clampInteger(source.layoutX + 220, 20, this.canvasWidth - this.pumpWidth - 20),
      layoutY: this.clampInteger(
        source.layoutY + 40,
        20,
        this.canvasHeight - this.getPumpHeight(source) - 20,
      ),
      label: `Pump ${pumpNumber}`,
      isActive: source.isActive,
      nozzles: source.nozzles.map((nozzle) => ({
        id: null,
        localId: createLocalId('nozzle'),
        productId: nozzle.productId,
        productCode: nozzle.productCode,
        productName: nozzle.productName,
        nozzleNumber: nozzle.nozzleNumber,
        fccNozzleNumber: nozzle.fccNozzleNumber,
        label: `P${pumpNumber}-N${nozzle.nozzleNumber}`,
        state: 'Idle',
        isActive: nozzle.isActive,
      })),
    };

    this.forecourt.pumps.push(clonedPump);
    this.sortForecourt();
    this.selectPump(clonedPump.localId);
    this.markDirty(`Cloned pump ${source.pumpNumber} into the draft.`);
  }

  removeSelectedPump(): void {
    if (!this.forecourt || !this.selectedPump) {
      return;
    }

    const removedPump = this.selectedPump;
    this.forecourt.pumps = this.forecourt.pumps.filter(
      (pump) => pump.localId !== removedPump.localId,
    );
    this.selectedPumpLocalId = this.forecourt.pumps[0]?.localId ?? null;
    this.selectedNozzleLocalId = this.selectedPump?.nozzles[0]?.localId ?? null;
    this.markDirty(`Removed pump ${removedPump.pumpNumber} from the draft.`);
  }

  addNozzle(): void {
    const pump = this.selectedPump;
    if (!pump) {
      return;
    }

    const product = this.defaultProductForIndex(pump.nozzles.length);
    if (!product) {
      this.errorMessage = 'Create or activate a product before assigning nozzles.';
      return;
    }

    const nozzleNumber = this.nextNozzleNumber(pump);
    const nozzle: DesignerNozzle = {
      id: null,
      localId: createLocalId('nozzle'),
      productId: product.id,
      productCode: product.productCode,
      productName: product.name,
      nozzleNumber,
      fccNozzleNumber: this.nextFccNozzleNumber(pump),
      label: `P${pump.pumpNumber}-N${nozzleNumber}`,
      state: 'Idle',
      isActive: true,
    };

    pump.nozzles.push(nozzle);
    pump.nozzles.sort((left, right) => left.nozzleNumber - right.nozzleNumber);
    this.selectedNozzleLocalId = nozzle.localId;
    this.markDirty(`Added nozzle ${nozzle.nozzleNumber} to pump ${pump.pumpNumber}.`);
  }

  removeNozzle(nozzleLocalId: string): void {
    const pump = this.selectedPump;
    if (!pump) {
      return;
    }

    const removed = pump.nozzles.find((nozzle) => nozzle.localId === nozzleLocalId);
    pump.nozzles = pump.nozzles.filter((nozzle) => nozzle.localId !== nozzleLocalId);
    this.selectedNozzleLocalId = pump.nozzles[0]?.localId ?? null;
    this.markDirty(
      removed
        ? `Removed nozzle ${removed.nozzleNumber} from pump ${pump.pumpNumber}.`
        : 'Removed nozzle from the draft.',
    );
  }

  selectPump(pumpLocalId: string | null): void {
    this.selectedPumpLocalId = pumpLocalId;
    this.selectedNozzleLocalId = this.selectedPump?.nozzles[0]?.localId ?? null;
  }

  selectNozzle(pumpLocalId: string, nozzleLocalId: string): void {
    this.selectedPumpLocalId = pumpLocalId;
    this.selectedNozzleLocalId = nozzleLocalId;
  }

  updatePumpNumber(pumpLocalId: string, value: number | string | null): void {
    const pump = this.findPump(pumpLocalId);
    if (!pump) {
      return;
    }

    pump.pumpNumber = this.coercePositiveInteger(value, pump.pumpNumber);
    this.sortForecourt();
    this.markDirty();
  }

  updateFccPumpNumber(pumpLocalId: string, value: number | string | null): void {
    const pump = this.findPump(pumpLocalId);
    if (!pump) {
      return;
    }

    pump.fccPumpNumber = this.coercePositiveInteger(value, pump.fccPumpNumber);
    this.markDirty();
  }

  updatePumpLabel(pumpLocalId: string, value: string): void {
    const pump = this.findPump(pumpLocalId);
    if (!pump) {
      return;
    }

    pump.label = value;
    this.markDirty();
  }

  updatePumpActive(pumpLocalId: string, value: boolean): void {
    const pump = this.findPump(pumpLocalId);
    if (!pump) {
      return;
    }

    pump.isActive = value;
    this.markDirty();
  }

  updateNozzleNumber(nozzleLocalId: string, value: number | string | null): void {
    const nozzle = this.findNozzle(nozzleLocalId);
    if (!nozzle) {
      return;
    }

    nozzle.nozzleNumber = this.coercePositiveInteger(value, nozzle.nozzleNumber);
    this.selectedPump?.nozzles.sort((left, right) => left.nozzleNumber - right.nozzleNumber);
    this.markDirty();
  }

  updateFccNozzleNumber(nozzleLocalId: string, value: number | string | null): void {
    const nozzle = this.findNozzle(nozzleLocalId);
    if (!nozzle) {
      return;
    }

    nozzle.fccNozzleNumber = this.coercePositiveInteger(value, nozzle.fccNozzleNumber);
    this.markDirty();
  }

  updateNozzleLabel(nozzleLocalId: string, value: string): void {
    const nozzle = this.findNozzle(nozzleLocalId);
    if (!nozzle) {
      return;
    }

    nozzle.label = value;
    this.markDirty();
  }

  updateNozzleProduct(nozzleLocalId: string, productId: string): void {
    const nozzle = this.findNozzle(nozzleLocalId);
    const product = this.products.find((item) => item.id === productId);
    if (!nozzle || !product) {
      return;
    }

    nozzle.productId = product.id;
    nozzle.productCode = product.productCode;
    nozzle.productName = product.name;
    this.markDirty();
  }

  updateNozzleActive(nozzleLocalId: string, value: boolean): void {
    const nozzle = this.findNozzle(nozzleLocalId);
    if (!nozzle) {
      return;
    }

    nozzle.isActive = value;
    this.markDirty();
  }

  saveForecourt(): void {
    if (!this.forecourt || !this.selectedSiteId || this.saving) {
      return;
    }

    this.saving = true;
    this.clearMessages();

    this.labApi
      .saveForecourt(this.selectedSiteId, this.toSaveRequest(this.forecourt))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (view) => {
          this.saving = false;
          this.forecourt = this.toDesignerForecourt(view);
          this.sortForecourt();
          this.ensureSelection();
          this.dirty = false;
          this.actionMessage = `Saved ${view.pumps.length} pumps and ${view.pumps.reduce((count, pump) => count + pump.nozzles.length, 0)} nozzles for ${view.siteCode}.`;
          this.refreshSites();
        },
        error: (error) => {
          this.saving = false;
          this.handleError(error, 'Forecourt save failed.');
        },
      });
  }

  beginDrag(event: PointerEvent, pumpLocalId: string): void {
    const pump = this.findPump(pumpLocalId);
    if (!pump) {
      return;
    }

    event.preventDefault();
    this.selectPump(pumpLocalId);
    this.dragState = {
      pumpLocalId,
      pointerId: event.pointerId,
      originX: pump.layoutX,
      originY: pump.layoutY,
      startClientX: event.clientX,
      startClientY: event.clientY,
    };
  }

  @HostListener('window:pointermove', ['$event'])
  handlePointerMove(event: PointerEvent): void {
    if (!this.dragState || event.pointerId !== this.dragState.pointerId || !this.canvasRef) {
      return;
    }

    const pump = this.findPump(this.dragState.pumpLocalId);
    if (!pump) {
      this.dragState = null;
      return;
    }

    const rect = this.canvasRef.nativeElement.getBoundingClientRect();
    if (!rect.width || !rect.height) {
      return;
    }

    const deltaX = ((event.clientX - this.dragState.startClientX) / rect.width) * this.canvasWidth;
    const deltaY =
      ((event.clientY - this.dragState.startClientY) / rect.height) * this.canvasHeight;
    const maxX = this.canvasWidth - this.pumpWidth - 24;
    const maxY = this.canvasHeight - this.getPumpHeight(pump) - 24;

    pump.layoutX = this.snapToGrid(
      this.clampInteger(Math.round(this.dragState.originX + deltaX), 20, maxX),
    );
    pump.layoutY = this.snapToGrid(
      this.clampInteger(Math.round(this.dragState.originY + deltaY), 20, maxY),
    );
    this.markDirty();
  }

  @HostListener('window:pointerup', ['$event'])
  @HostListener('window:pointercancel', ['$event'])
  handlePointerStop(event: PointerEvent): void {
    if (this.dragState && event.pointerId === this.dragState.pointerId) {
      this.dragState = null;
    }
  }

  trackByLocalId(_index: number, item: { localId: string }): string {
    return item.localId;
  }

  productColor(productId: string): string {
    return this.products.find((product) => product.id === productId)?.colorHex ?? '#9ca3af';
  }

  getPumpHeight(pump: DesignerPump): number {
    return (
      this.pumpHeaderHeight + 22 + pump.nozzles.length * (this.nozzleRowHeight + this.nozzleGap)
    );
  }

  displayPumpBodyHeight(pump: DesignerPump): number {
    return this.getPumpHeight(pump) - 18;
  }

  private loadCatalog(preferredSiteId?: string): void {
    this.loadingCatalog = true;
    this.clearMessages();

    forkJoin({
      sites: this.labApi.getSites(true),
      products: this.labApi.getProducts(true),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ sites, products }) => {
          this.loadingCatalog = false;
          this.sites = sites;
          this.products = products;

          const selectedSiteId =
            preferredSiteId && sites.some((site) => site.id === preferredSiteId)
              ? preferredSiteId
              : (sites.find((site) => site.isActive)?.id ?? sites[0]?.id ?? null);

          if (selectedSiteId) {
            this.loadForecourt(selectedSiteId);
          }
        },
        error: (error) => {
          this.loadingCatalog = false;
          this.handleError(error, 'Unable to load sites and products.');
        },
      });
  }

  private refreshSites(): void {
    this.labApi
      .getSites(true)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (sites) => {
          this.sites = sites;
        },
      });
  }

  private loadForecourt(siteId: string): void {
    this.loadingForecourt = true;
    this.clearMessages();

    this.labApi
      .getForecourt(siteId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (view) => {
          this.loadingForecourt = false;
          this.forecourt = this.toDesignerForecourt(view);
          this.selectedSiteId = siteId;
          this.sortForecourt();
          this.ensureSelection();
          this.dirty = false;
        },
        error: (error) => {
          this.loadingForecourt = false;
          this.handleError(error, 'Unable to load the selected forecourt.');
        },
      });
  }

  private toDesignerForecourt(view: SiteForecourtView): DesignerForecourt {
    return {
      siteId: view.siteId,
      siteCode: view.siteCode,
      siteName: view.siteName,
      pumps: view.pumps.map((pump) => ({
        id: pump.id,
        localId: pump.id,
        pumpNumber: pump.pumpNumber,
        fccPumpNumber: pump.fccPumpNumber,
        layoutX: pump.layoutX,
        layoutY: pump.layoutY,
        label: pump.label,
        isActive: pump.isActive,
        nozzles: pump.nozzles.map((nozzle) => ({
          id: nozzle.id,
          localId: nozzle.id,
          productId: nozzle.productId,
          productCode: nozzle.productCode,
          productName: nozzle.productName,
          nozzleNumber: nozzle.nozzleNumber,
          fccNozzleNumber: nozzle.fccNozzleNumber,
          label: nozzle.label,
          state: nozzle.state,
          isActive: nozzle.isActive,
        })),
      })),
    };
  }

  private toSaveRequest(forecourt: DesignerForecourt): SaveForecourtRequest {
    return {
      pumps: forecourt.pumps.map((pump) => ({
        id: pump.id,
        pumpNumber: pump.pumpNumber,
        fccPumpNumber: pump.fccPumpNumber,
        layoutX: pump.layoutX,
        layoutY: pump.layoutY,
        label: pump.label,
        isActive: pump.isActive,
        nozzles: pump.nozzles.map<ForecourtNozzleUpsertRequest>((nozzle) => ({
          id: nozzle.id,
          productId: nozzle.productId,
          nozzleNumber: nozzle.nozzleNumber,
          fccNozzleNumber: nozzle.fccNozzleNumber,
          label: nozzle.label,
          isActive: nozzle.isActive,
        })),
      })),
    };
  }

  private createPumpDraft(
    pumpNumber: number,
    fccPumpNumber: number,
    nozzleCount: number,
    layoutX: number,
    layoutY: number,
  ): DesignerPump {
    return {
      id: null,
      localId: createLocalId('pump'),
      pumpNumber,
      fccPumpNumber,
      layoutX,
      layoutY,
      label: `Pump ${pumpNumber}`,
      isActive: true,
      nozzles: Array.from({ length: nozzleCount }, (_value, index) =>
        this.createNozzleDraft(pumpNumber, index + 1),
      ),
    };
  }

  private createNozzleDraft(pumpNumber: number, nozzleNumber: number): DesignerNozzle {
    const product = this.defaultProductForIndex(nozzleNumber - 1);

    return {
      id: null,
      localId: createLocalId('nozzle'),
      productId: product?.id ?? '',
      productCode: product?.productCode ?? 'UNASSIGNED',
      productName: product?.name ?? 'Select a product',
      nozzleNumber,
      fccNozzleNumber: nozzleNumber,
      label: `P${pumpNumber}-N${nozzleNumber}`,
      state: 'Idle',
      isActive: true,
    };
  }

  private defaultProductForIndex(index: number): ProductView | null {
    const productOptions = this.activeProductOptions;
    if (!productOptions.length) {
      return null;
    }

    return productOptions[index % productOptions.length];
  }

  private nextPumpNumber(): number {
    const numbers = this.forecourt?.pumps.map((pump) => pump.pumpNumber) ?? [];
    return Math.max(0, ...numbers) + 1;
  }

  private nextFccPumpNumber(): number {
    const numbers = this.forecourt?.pumps.map((pump) => pump.fccPumpNumber) ?? [];
    return Math.max(0, ...numbers) + 1;
  }

  private nextNozzleNumber(pump: DesignerPump): number {
    return Math.max(0, ...pump.nozzles.map((nozzle) => nozzle.nozzleNumber), 0) + 1;
  }

  private nextFccNozzleNumber(pump: DesignerPump): number {
    return Math.max(0, ...pump.nozzles.map((nozzle) => nozzle.fccNozzleNumber), 0) + 1;
  }

  private defaultPumpPosition(ordinal: number): { x: number; y: number } {
    const column = ordinal % 5;
    const row = Math.floor(ordinal / 5);
    return {
      x: 120 + column * 240,
      y: 100 + row * 250,
    };
  }

  private canProvisionNozzles(nozzleCount: number): boolean {
    if (nozzleCount === 0 || this.activeProductOptions.length > 0) {
      return true;
    }

    this.errorMessage = 'Add or activate at least one product before creating nozzles.';
    return false;
  }

  private sortForecourt(): void {
    if (!this.forecourt) {
      return;
    }

    this.forecourt.pumps.sort((left, right) => left.pumpNumber - right.pumpNumber);
    for (const pump of this.forecourt.pumps) {
      pump.nozzles.sort((left, right) => left.nozzleNumber - right.nozzleNumber);
    }
  }

  private ensureSelection(): void {
    if (!this.forecourt) {
      this.selectedPumpLocalId = null;
      this.selectedNozzleLocalId = null;
      return;
    }

    if (!this.forecourt.pumps.some((pump) => pump.localId === this.selectedPumpLocalId)) {
      this.selectedPumpLocalId = this.forecourt.pumps[0]?.localId ?? null;
    }

    const currentPump = this.selectedPump;
    if (!currentPump) {
      this.selectedNozzleLocalId = null;
      return;
    }

    if (!currentPump.nozzles.some((nozzle) => nozzle.localId === this.selectedNozzleLocalId)) {
      this.selectedNozzleLocalId = currentPump.nozzles[0]?.localId ?? null;
    }
  }

  private findPump(pumpLocalId: string): DesignerPump | null {
    return this.forecourt?.pumps.find((pump) => pump.localId === pumpLocalId) ?? null;
  }

  private findNozzle(nozzleLocalId: string): DesignerNozzle | null {
    return (
      this.forecourt?.pumps
        .flatMap((pump) => pump.nozzles)
        .find((nozzle) => nozzle.localId === nozzleLocalId) ?? null
    );
  }

  private markDirty(message?: string): void {
    this.dirty = true;
    this.actionMessage = message ?? this.actionMessage;
    this.errorMessage = '';
  }

  private clearMessages(): void {
    this.actionMessage = '';
    this.errorMessage = '';
    this.validationMessages = [];
  }

  private handleError(error: unknown, fallbackMessage: string): void {
    this.actionMessage = '';
    this.errorMessage = fallbackMessage;
    this.validationMessages = [];

    if (!(error instanceof HttpErrorResponse)) {
      return;
    }

    const response = error.error as ManagementErrorResponse | string | null;
    if (response && typeof response === 'object' && Array.isArray(response.errors)) {
      this.errorMessage = response.message || fallbackMessage;
      this.validationMessages = response.errors;
      return;
    }

    if (typeof response === 'string' && response.trim()) {
      this.errorMessage = response;
    }
  }

  private confirmDiscardChanges(): boolean {
    return !this.dirty || globalThis.confirm('Discard the current unsaved forecourt changes?');
  }

  private snapToGrid(value: number): number {
    return Math.round(value / 20) * 20;
  }

  private clampInteger(value: number, min: number, max: number): number {
    return Math.min(Math.max(value, min), max);
  }

  private coercePositiveInteger(value: number | string | null, fallback: number): number {
    const parsed = Number(value);
    if (!Number.isFinite(parsed) || parsed <= 0) {
      return fallback;
    }

    return Math.round(parsed);
  }
}

function createLocalId(prefix: string): string {
  if (typeof globalThis.crypto?.randomUUID === 'function') {
    return `${prefix}-${globalThis.crypto.randomUUID()}`;
  }

  return `${prefix}-${Math.random().toString(36).slice(2, 10)}`;
}
