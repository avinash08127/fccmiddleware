import { inject, provideAppInitializer, signal, computed } from '@angular/core';
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

const APP_ROLES = ['FccAdmin', 'FccUser', 'FccViewer'] as const;

export type AppRole = (typeof APP_ROLES)[number];

/** Common role sets for route guards and directives */
export const ALL_ROLES: AppRole[] = ['FccAdmin', 'FccUser', 'FccViewer'];
export const WRITE_ROLES: AppRole[] = ['FccAdmin', 'FccUser'];
export const ADMIN_ROLES: AppRole[] = ['FccAdmin'];

/**
 * Reactive store for the current user's role and legal entities,
 * populated from the backend after MSAL login.
 */
export const currentUserRole = signal<AppRole | null>(null);
export const currentUserLegalEntities = signal<Array<{ id: string; name: string; countryCode: string }>>([]);
export const currentUserAllLegalEntities = signal<boolean>(false);
export const currentUserDisplayName = signal<string>('');
export const currentUserEmail = signal<string>('');
export const isUserProvisioned = signal<boolean>(true);

export const isAdmin = computed(() => currentUserRole() === 'FccAdmin');
export const isWriter = computed(() => {
  const role = currentUserRole();
  return role === 'FccAdmin' || role === 'FccUser';
});

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

    // Set active account from cache if available (returning user).
    // Do NOT call handleRedirectPromise() here — that is handled by
    // MsalService.handleRedirectObservable() in the App component.
    // Calling it here would consume the redirect response before
    // MsalGuard can observe it, causing a blank page.
    syncActiveAccount(instance);
  })();

  initializationByInstance.set(instance, initialization);
  return initialization;
}

export function getCurrentAccount(instance: IPublicClientApplication): AccountInfo | null {
  return instance.getActiveAccount() ?? instance.getAllAccounts()[0] ?? null;
}

/**
 * Returns the user's role from the backend-populated signal.
 * Falls back to empty array if not yet loaded.
 */
export function getAccountRoles(_account: AccountInfo | null | undefined): AppRole[] {
  const role = currentUserRole();
  return role ? [role] : [];
}

export function getPrimaryRoleLabel(_account: AccountInfo | null | undefined): string {
  return currentUserRole() ?? '';
}

export function hasAnyRequiredRole(
  _account: AccountInfo | null | undefined,
  requiredRoles: readonly AppRole[],
): boolean {
  const role = currentUserRole();
  if (!role) return false;
  return requiredRoles.includes(role);
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
      currentUserRole.set(null);
      currentUserLegalEntities.set([]);
      currentUserAllLegalEntities.set(false);
      currentUserDisplayName.set('');
      currentUserEmail.set('');
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
