import { Directive, Input, OnInit, TemplateRef, ViewContainerRef, inject } from '@angular/core';
import { MsalService } from '@azure/msal-angular';
import { AppRole } from '../../core/auth/role.guard';

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
    const account = this.msal.instance.getActiveAccount();
    if (!account) {
      this.viewContainer.clear();
      return;
    }

    const claims = account.idTokenClaims as Record<string, unknown>;
    const userRoles: string[] = Array.isArray(claims?.['roles'])
      ? (claims['roles'] as string[])
      : [];

    const hasRole = this.requiredRoles.some((r) => userRoles.includes(r));

    if (hasRole) {
      if (this.viewContainer.length === 0) {
        this.viewContainer.createEmbeddedView(this.templateRef);
      }
    } else {
      this.viewContainer.clear();
    }
  }
}
