import { Directive, DestroyRef, Input, OnInit, TemplateRef, ViewContainerRef, inject, effect } from '@angular/core';
import { AppRole, currentUserRole } from '../../core/auth/auth-state';

/**
 * Structural directive that shows or hides an element based on user role.
 *
 * Reads the role from the backend-populated signal (currentUserRole),
 * so changes are reflected reactively.
 *
 * Usage:
 *   <button *appRoleVisible="['FccAdmin', 'FccUser']">Edit</button>
 *
 *   Or with the microsyntax alias:
 *   <div [appRoleVisible]="['FccAdmin']">Admin-only content</div>
 */
@Directive({
  selector: '[appRoleVisible]',
  standalone: true,
})
export class RoleVisibleDirective implements OnInit {
  private readonly templateRef = inject(TemplateRef<unknown>);
  private readonly viewContainer = inject(ViewContainerRef);

  private requiredRoles: AppRole[] = [];

  @Input() set appRoleVisible(roles: AppRole[]) {
    this.requiredRoles = roles;
    this.updateView();
  }

  constructor() {
    // Reactively update when the role signal changes
    effect(() => {
      currentUserRole(); // track
      this.updateView();
    });
  }

  ngOnInit(): void {
    this.updateView();
  }

  private updateView(): void {
    const role = currentUserRole();
    if (!role) {
      this.viewContainer.clear();
      return;
    }

    if (this.requiredRoles.includes(role)) {
      if (this.viewContainer.length === 0) {
        this.viewContainer.createEmbeddedView(this.templateRef);
      }
    } else {
      this.viewContainer.clear();
    }
  }
}
