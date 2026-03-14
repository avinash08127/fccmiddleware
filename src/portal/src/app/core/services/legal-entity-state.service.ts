import { inject, Injectable, signal, computed } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MasterDataService } from './master-data.service';
import { LegalEntity } from '../models/master-data.model';

/**
 * Shared singleton that owns the legal-entity list and the current selection.
 *
 * Every page that needs a legal-entity dropdown reads from this service
 * instead of maintaining its own local signals. The selection persists
 * across navigation so users don't have to re-pick on every page.
 */
@Injectable({ providedIn: 'root' })
export class LegalEntityStateService {
  private readonly masterData = inject(MasterDataService);

  private readonly _entities = signal<LegalEntity[]>([]);
  private readonly _selectedId = signal<string | null>(null);
  private readonly _loaded = signal(false);
  private readonly _error = signal(false);

  /** Sorted alphabetically by name. */
  readonly entities = this._entities.asReadonly();
  readonly selectedId = this._selectedId.asReadonly();
  readonly loaded = this._loaded.asReadonly();
  readonly error = this._error.asReadonly();

  readonly options = computed(() =>
    this._entities().map((e) => ({
      label: `${e.name} (${e.code})`,
      value: e.id,
    })),
  );

  constructor() {
    this.masterData
      .getLegalEntities()
      .pipe(takeUntilDestroyed())
      .subscribe({
        next: (entities) => {
          const sorted = [...entities].sort((a, b) =>
            a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }),
          );
          this._entities.set(sorted);
          this._loaded.set(true);

          // Auto-select first entity if nothing is selected yet.
          if (!this._selectedId() && sorted.length > 0) {
            this._selectedId.set(sorted[0].id);
          }
        },
        error: () => {
          this._error.set(true);
          this._loaded.set(true);
        },
      });
  }

  select(id: string | null): void {
    this._selectedId.set(id);
  }

  nameOf(id: string): string {
    const entity = this._entities().find((e) => e.id === id);
    return entity ? `${entity.name} (${entity.code})` : id;
  }
}
