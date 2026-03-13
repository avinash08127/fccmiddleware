import { Directive, DestroyRef, Input, OnInit, TemplateRef, ViewContainerRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MsalService, MsalBroadcastService } from '@azure/msal-angular';
import { InteractionStatus } from '@azure/msal-browser';
import { filter } from 'rxjs/operators';
import { AppRole, getCurrentAccount, hasAnyRequiredRole } from '../../core/auth/auth-state';

/**
 * Structural directive that shows or hides an element based on user role.
 *
 * Re-evaluates whenever MSAL completes an interaction (login, token refresh),
 * so role changes mid-session are reflected without a page reload.
 *
 * Usage:
 *   <button *appRoleVisible="['SystemAdmin', 'OperationsManager']">Edit</button>
 *
 *   Or with the microsyntax alias:
 *   <div [appRoleVisible]="['SystemAdmin']">Admin-only content</div>
 */
@Directive({
  selector: '[appRoleVisible]',
  standalone: true,
})
export class RoleVisibleDirective implements OnInit {
  private readonly msal = inject(MsalService);
  private readonly broadcast = inject(MsalBroadcastService);
  private readonly templateRef = inject(TemplateRef<unknown>);
  private readonly viewContainer = inject(ViewContainerRef);
  private readonly destroyRef = inject(DestroyRef);

  private requiredRoles: AppRole[] = [];

  @Input() set appRoleVisible(roles: AppRole[]) {
    this.requiredRoles = roles;
    this.updateView();
  }

  ngOnInit(): void {
    this.updateView();

    this.broadcast.inProgress$
      .pipe(
        filter((status) => status === InteractionStatus.None),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.updateView());
  }

  private updateView(): void {
    const account = getCurrentAccount(this.msal.instance);
    if (!account) {
      this.viewContainer.clear();
      return;
    }

    if (hasAnyRequiredRole(account, this.requiredRoles)) {
      if (this.viewContainer.length === 0) {
        this.viewContainer.createEmbeddedView(this.templateRef);
      }
    } else {
      this.viewContainer.clear();
    }
  }
}
