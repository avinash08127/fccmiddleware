import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-empty-state',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="empty-state">
      <i [class]="'pi ' + icon + ' empty-state__icon'"></i>
      <h3 class="empty-state__title">{{ title }}</h3>
      @if (description) {
        <p class="empty-state__description">{{ description }}</p>
      }
      <ng-content />
    </div>
  `,
  styles: [`
    .empty-state {
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      padding: 3rem 1.5rem;
      text-align: center;
      gap: 0.5rem;
    }
    .empty-state__icon {
      font-size: 2.5rem;
      color: var(--p-text-muted-color, #94a3b8);
      margin-bottom: 0.5rem;
    }
    .empty-state__title {
      font-size: 1.125rem;
      font-weight: 600;
      margin: 0;
    }
    .empty-state__description {
      font-size: 0.875rem;
      color: var(--p-text-muted-color, #64748b);
      max-width: 360px;
      margin: 0;
    }
  `],
})
export class EmptyStateComponent {
  @Input() icon = 'pi-inbox';
  @Input() title = 'No data found';
  @Input() description = '';
}
