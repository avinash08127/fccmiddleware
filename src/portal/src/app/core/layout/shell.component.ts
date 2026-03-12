import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { CommonModule } from '@angular/common';
import { MsalService } from '@azure/msal-angular';
import { ButtonModule } from 'primeng/button';
import { AvatarModule } from 'primeng/avatar';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { getCurrentAccount, getPrimaryRoleLabel } from '../auth/auth-state';

interface NavItem {
  label: string;
  icon: string;
  route: string;
}

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [
    CommonModule,
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    ButtonModule,
    AvatarModule,
    ToastModule,
  ],
  providers: [MessageService],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
})
export class ShellComponent implements OnInit {
  private readonly msal = inject(MsalService);

  readonly navItems: NavItem[] = [
    { label: 'Dashboard', icon: 'pi pi-home', route: '/dashboard' },
    { label: 'Transactions', icon: 'pi pi-list', route: '/transactions' },
    { label: 'Reconciliation', icon: 'pi pi-check-square', route: '/reconciliation' },
    { label: 'Edge Agents', icon: 'pi pi-server', route: '/agents' },
    { label: 'Sites', icon: 'pi pi-map-marker', route: '/sites' },
    { label: 'Master Data', icon: 'pi pi-database', route: '/master-data' },
    { label: 'Audit Log', icon: 'pi pi-shield', route: '/audit' },
    { label: 'Dead-Letter Queue', icon: 'pi pi-inbox', route: '/dlq' },
    { label: 'Settings', icon: 'pi pi-cog', route: '/settings' },
  ];

  userName = signal<string>('');
  userRole = signal<string>('');
  activeLegalEntity = signal<string>('');

  ngOnInit(): void {
    const account = getCurrentAccount(this.msal.instance);
    if (account) {
      this.userName.set(account.name ?? account.username);
      this.userRole.set(getPrimaryRoleLabel(account));
    }
  }

  logout(): void {
    this.msal.logoutRedirect();
  }
}
