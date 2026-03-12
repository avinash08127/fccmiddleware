import { Directive, Input, OnInit, TemplateRef, ViewContainerRef, inject } from '@angular/core';
import { MsalService } from '@azure/msal-angular';
import { AppRole, getCurrentAccount, hasAnyRequiredRole } from '../../core/auth/auth-state';

/**
 * Structural directive that shows or hides an element based on user role.
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
  private readonly templateRef = inject(TemplateRef<unknown>);
  private readonly viewContainer = inject(ViewContainerRef);

  private requiredRoles: AppRole[] = [];

  @Input() set appRoleVisible(roles: AppRole[]) {
    this.requiredRoles = roles;
    this.updateView();
  }

  ngOnInit(): void {
    this.updateView();
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
