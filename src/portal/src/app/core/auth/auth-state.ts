import { inject, provideAppInitializer } from '@angular/core';
import { MsalService } from '@azure/msal-angular';
import {
  AccountInfo,
  AuthenticationResult,
  EventMessage,
  EventType,
  IPublicClientApplication,
} from '@azure/msal-browser';

const registeredInstances = new WeakSet<IPublicClientApplication>();
const initializationByInstance = new WeakMap<IPublicClientApplication, Promise<void>>();

const APP_ROLES = [
  'SystemAdmin',
  /** @deprecated Use 'SystemAdmin'. Kept for backward-compatible JWT recognition. */
  'SystemAdministrator',
  'OperationsManager',
  'SiteSupervisor',
  'Auditor',
  'SupportReadOnly',
] as const;

export type AppRole = (typeof APP_ROLES)[number];

export function providePortalAuth() {
  return provideAppInitializer(() => initializePortalAuth(inject(MsalService).instance));
}

export function initializePortalAuth(instance: IPublicClientApplication): Promise<void> {
  const existingInitialization = initializationByInstance.get(instance);
  if (existingInitialization) {
    return existingInitialization;
  }

  const initialization = (async () => {
    const initialize = instance.initialize?.bind(instance);
    if (initialize) {
      await initialize();
    }

    registerActiveAccountSync(instance);

    const result = await instance.handleRedirectPromise();
    syncActiveAccount(instance, result?.account ?? undefined);
  })();

  initializationByInstance.set(instance, initialization);
  return initialization;
}

export function getCurrentAccount(instance: IPublicClientApplication): AccountInfo | null {
  return instance.getActiveAccount() ?? instance.getAllAccounts()[0] ?? null;
}

export function getAccountRoles(account: AccountInfo | null | undefined): AppRole[] {
  const roles = new Set<AppRole>();

  for (const role of getRawRoles(account)) {
    if (isAppRole(role)) {
      roles.add(role);
    }
  }

  if (roles.has('SystemAdmin') || roles.has('SystemAdministrator')) {
    roles.add('SystemAdmin');
    roles.add('SystemAdministrator');
  }

  return [...roles];
}

export function getPrimaryRoleLabel(account: AccountInfo | null | undefined): string {
  const [primaryRole] = getRawRoles(account);
  return primaryRole ?? getAccountRoles(account)[0] ?? '';
}

export function hasAnyRequiredRole(
  account: AccountInfo | null | undefined,
  requiredRoles: readonly AppRole[],
): boolean {
  const userRoles = new Set(getAccountRoles(account));
  return requiredRoles.some((role) => userRoles.has(role));
}

function getRawRoles(account: AccountInfo | null | undefined): string[] {
  const claims = account?.idTokenClaims as Record<string, unknown> | undefined;
  const claimRoles = claims?.['roles'];

  if (Array.isArray(claimRoles)) {
    return claimRoles
      .flatMap((role) => (typeof role === 'string' ? role.split(',') : []))
      .map((role) => role.trim())
      .filter((role) => role.length > 0);
  }

  if (typeof claimRoles === 'string') {
    return claimRoles
      .split(',')
      .map((role) => role.trim())
      .filter((role) => role.length > 0);
  }

  return [];
}

function isAppRole(role: string): role is AppRole {
  return (APP_ROLES as readonly string[]).includes(role);
}

function registerActiveAccountSync(instance: IPublicClientApplication): void {
  if (registeredInstances.has(instance)) {
    return;
  }

  registeredInstances.add(instance);
  instance.addEventCallback((message) => handleMsalEvent(instance, message));
}

function handleMsalEvent(instance: IPublicClientApplication, message: EventMessage): void {
  switch (message.eventType) {
    case EventType.LOGIN_SUCCESS:
    case EventType.ACQUIRE_TOKEN_SUCCESS:
      syncActiveAccount(instance, getEventAccount(message.payload));
      break;
    case EventType.LOGOUT_SUCCESS:
      instance.setActiveAccount(null);
      break;
    default:
      break;
  }
}

function getEventAccount(payload: unknown): AccountInfo | undefined {
  return (payload as AuthenticationResult | null)?.account ?? undefined;
}

function syncActiveAccount(
  instance: IPublicClientApplication,
  account?: AccountInfo | null,
): void {
  instance.setActiveAccount(account ?? getCurrentAccount(instance));
}
