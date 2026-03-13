import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet, Router } from '@angular/router';
import { CommonModule } from '@angular/common';
import { MsalService } from '@azure/msal-angular';
import { ButtonModule } from 'primeng/button';
import { AvatarModule } from 'primeng/avatar';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import {
  getCurrentAccount,
  getPrimaryRoleLabel,
  currentUserRole,
  currentUserDisplayName,
  isAdmin,
  isUserProvisioned,
} from '../auth/auth-state';
import { PortalUserService } from '../auth/portal-user.service';

interface NavItem {
  label: string;
  icon: string;
  route: string;
  adminOnly?: boolean;
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
  private readonly portalUserService = inject(PortalUserService);
  private readonly router = inject(Router);

  private readonly allNavItems: NavItem[] = [
    { label: 'Dashboard', icon: 'pi pi-home', route: '/dashboard' },
    { label: 'Transactions', icon: 'pi pi-list', route: '/transactions' },
    { label: 'Reconciliation', icon: 'pi pi-check-square', route: '/reconciliation' },
    { label: 'Edge Agents', icon: 'pi pi-server', route: '/agents' },
    { label: 'Sites', icon: 'pi pi-map-marker', route: '/sites' },
    { label: 'Adapters', icon: 'pi pi-sliders-h', route: '/adapters' },
    { label: 'Audit Log', icon: 'pi pi-shield', route: '/audit' },
    { label: 'Dead-Letter Queue', icon: 'pi pi-inbox', route: '/dlq' },
    { label: 'Settings', icon: 'pi pi-cog', route: '/settings' },
    { label: 'User Management', icon: 'pi pi-users', route: '/admin/users', adminOnly: true },
  ];

  readonly navItems = computed(() => {
    const admin = isAdmin();
    return this.allNavItems.filter((item) => !item.adminOnly || admin);
  });

  userName = computed(() => currentUserDisplayName() || this.getAccountName());
  userRole = computed(() => getPrimaryRoleLabel(null));
  activeLegalEntity = signal<string>('');

  async ngOnInit(): Promise<void> {
    if (!this.portalUserService.isLoaded) {
      const success = await this.portalUserService.loadCurrentUser();
      if (!success && !isUserProvisioned()) {
        this.router.navigate(['/access-denied']);
        return;
      }
    }
  }

  logout(): void {
    this.msal.logoutRedirect();
  }

  private getAccountName(): string {
    const account = getCurrentAccount(this.msal.instance);
    return account?.name ?? account?.username ?? '';
  }
}
