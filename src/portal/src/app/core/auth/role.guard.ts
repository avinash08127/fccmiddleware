import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { AppRole, getCurrentAccount, hasAnyRequiredRole } from './auth-state';

/**
 * Route guard that reads the `roles` claim from the Entra ID JWT.
 * Usage: canActivate: [roleGuard(['SystemAdmin', 'OperationsManager'])]
 */
export function roleGuard(requiredRoles: AppRole[]): CanActivateFn {
  return () => {
    const msal = inject(MsalService);
    const router = inject(Router);

    const account = getCurrentAccount(msal.instance);
    if (!account) {
      return router.parseUrl('/access-denied');
    }

    if (!hasAnyRequiredRole(account, requiredRoles)) {
      return router.parseUrl('/access-denied');
    }

    return true;
  };
}
