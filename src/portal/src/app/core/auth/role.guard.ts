import { inject } from '@angular/core';
import { CanActivateFn, Router, ActivatedRouteSnapshot } from '@angular/router';
import { MsalService } from '@azure/msal-angular';

export type AppRole = 'SystemAdmin' | 'OperationsManager' | 'SiteSupervisor' | 'Auditor';

/**
 * Route guard that reads the `roles` claim from the Entra ID JWT.
 * Usage: canActivate: [roleGuard(['SystemAdmin', 'OperationsManager'])]
 */
export function roleGuard(requiredRoles: AppRole[]): CanActivateFn {
  return (_route: ActivatedRouteSnapshot) => {
    const msal = inject(MsalService);
    const router = inject(Router);

    const account = msal.instance.getActiveAccount();
    if (!account) {
      router.navigate(['/access-denied']);
      return false;
    }

    const tokenClaims = account.idTokenClaims as Record<string, unknown>;
    const userRoles: string[] = Array.isArray(tokenClaims?.['roles'])
      ? (tokenClaims['roles'] as string[])
      : [];

    const hasRole = requiredRoles.some((r) => userRoles.includes(r));
    if (!hasRole) {
      router.navigate(['/access-denied']);
      return false;
    }

    return true;
  };
}
