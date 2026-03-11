import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ProgressSpinnerModule } from 'primeng/progressspinner';

@Component({
  selector: 'app-loading-spinner',
  standalone: true,
  imports: [CommonModule, ProgressSpinnerModule],
  template: `
    @if (visible) {
      <div class="loading-spinner-overlay" [class.loading-spinner-overlay--inline]="inline">
        <p-progressSpinner
          [style]="{ width: size, height: size }"
          strokeWidth="4"
          animationDuration=".8s"
        />
        @if (message) {
          <p class="loading-spinner__message">{{ message }}</p>
        }
      </div>
    }
  `,
  styles: [`
    .loading-spinner-overlay {
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      position: absolute;
      inset: 0;
      background: rgba(255, 255, 255, 0.7);
      z-index: 10;
      gap: 0.5rem;
    }
    .loading-spinner-overlay--inline {
      position: relative;
      padding: 2rem;
      background: transparent;
    }
    .loading-spinner__message {
      font-size: 0.875rem;
      color: var(--p-text-muted-color, #64748b);
    }
  `],
})
export class LoadingSpinnerComponent {
  @Input() visible = true;
  @Input() message = '';
  @Input() size = '40px';
  @Input() inline = false;
}
