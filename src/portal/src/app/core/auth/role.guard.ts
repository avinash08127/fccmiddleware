import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AppRole, currentUserRole } from './auth-state';

/**
 * Route guard that checks the user's locally-managed role
 * (populated from the backend via PortalUserService).
 * Usage: canActivate: [roleGuard(['FccAdmin', 'FccUser'])]
 */
export function roleGuard(requiredRoles: AppRole[]): CanActivateFn {
  return () => {
    const router = inject(Router);
    const role = currentUserRole();

    if (!role) {
      return router.parseUrl('/access-denied');
    }

    if (!requiredRoles.includes(role)) {
      return router.parseUrl('/access-denied');
    }

    return true;
  };
}
