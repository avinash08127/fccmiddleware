import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TagModule } from 'primeng/tag';

export type StatusSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

@Component({
  selector: 'app-status-badge',
  standalone: true,
  imports: [CommonModule, TagModule],
  template: `<p-tag [value]="label" [severity]="severity" [rounded]="true" />`,
})
export class StatusBadgeComponent {
  @Input() label = '';
  @Input() severity: StatusSeverity = 'info';
}
