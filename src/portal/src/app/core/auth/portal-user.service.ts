import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { MsalService } from '@azure/msal-angular';
import { firstValueFrom } from 'rxjs';
import {
  AppRole,
  currentUserRole,
  currentUserLegalEntities,
  currentUserAllLegalEntities,
  currentUserDisplayName,
  currentUserEmail,
  isUserProvisioned,
  getCurrentAccount,
} from './auth-state';

export interface CurrentUserResponse {
  id: string;
  email: string;
  displayName: string;
  role: AppRole;
  allLegalEntities: boolean;
  legalEntities: Array<{ id: string; name: string; countryCode: string }>;
}

@Injectable({ providedIn: 'root' })
export class PortalUserService {
  private readonly http = inject(HttpClient);
  private readonly msal = inject(MsalService);
  private loaded = false;

  /**
   * Fetches the current user's profile from the backend.
   * Called once after MSAL login succeeds.
   */
  async loadCurrentUser(): Promise<boolean> {
    const account = getCurrentAccount(this.msal.instance);
    if (!account) {
      isUserProvisioned.set(false);
      return false;
    }

    try {
      const user = await firstValueFrom(
        this.http.get<CurrentUserResponse>('/api/v1/admin/users/me')
      );

      currentUserRole.set(user.role);
      currentUserLegalEntities.set(user.legalEntities);
      currentUserAllLegalEntities.set(user.allLegalEntities);
      currentUserDisplayName.set(user.displayName);
      currentUserEmail.set(user.email);
      isUserProvisioned.set(true);
      this.loaded = true;
      return true;
    } catch (err: unknown) {
      const status = (err as { status?: number })?.status;
      if (status === 403) {
        isUserProvisioned.set(false);
        return false;
      }
      // Other errors (network, 500) — let the user retry
      console.error('Failed to load current user', err);
      isUserProvisioned.set(false);
      return false;
    }
  }

  get isLoaded(): boolean {
    return this.loaded;
  }
}
