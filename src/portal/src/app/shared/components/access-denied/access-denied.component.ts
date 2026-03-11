import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ButtonModule } from 'primeng/button';

@Component({
  selector: 'app-access-denied',
  standalone: true,
  imports: [RouterLink, ButtonModule],
  template: `
    <div class="access-denied">
      <i class="pi pi-lock access-denied__icon"></i>
      <h2>Access Denied</h2>
      <p>You do not have permission to view this page.</p>
      <p-button label="Go to Dashboard" routerLink="/dashboard" />
    </div>
  `,
  styles: [`
    .access-denied {
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      height: 100%;
      gap: 1rem;
      text-align: center;
    }
    .access-denied__icon {
      font-size: 3rem;
      color: var(--p-red-500, #ef4444);
    }
  `],
})
export class AccessDeniedComponent {}
