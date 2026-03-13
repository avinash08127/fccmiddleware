import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface PortalUserDto {
  id: string;
  email: string;
  displayName: string;
  role: string;
  allLegalEntities: boolean;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  legalEntities: LegalEntitySummary[];
}

export interface LegalEntitySummary {
  id: string;
  name: string;
  countryCode: string;
}

export interface UserListResponse {
  items: PortalUserDto[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export interface CreateUserRequest {
  email: string;
  displayName: string;
  role: string;
  legalEntityIds: string[];
  allLegalEntities: boolean;
}

export interface UpdateUserRequest {
  role?: string;
  legalEntityIds?: string[];
  allLegalEntities?: boolean;
  isActive?: boolean;
}

export interface RoleDto {
  id: number;
  name: string;
}

@Injectable({ providedIn: 'root' })
export class UserManagementService {
  private readonly http = inject(HttpClient);

  listUsers(
    page = 1,
    pageSize = 25,
    filters?: {
      role?: string;
      legalEntityId?: string;
      isActive?: boolean;
      search?: string;
    },
  ): Observable<UserListResponse> {
    let params = new HttpParams()
      .set('page', page.toString())
      .set('pageSize', pageSize.toString());

    if (filters?.role) params = params.set('role', filters.role);
    if (filters?.legalEntityId) params = params.set('legalEntityId', filters.legalEntityId);
    if (filters?.isActive !== undefined) params = params.set('isActive', filters.isActive.toString());
    if (filters?.search) params = params.set('search', filters.search);

    return this.http.get<UserListResponse>('/api/v1/admin/users', { params });
  }

  getUser(id: string): Observable<PortalUserDto> {
    return this.http.get<PortalUserDto>(`/api/v1/admin/users/${id}`);
  }

  createUser(request: CreateUserRequest): Observable<{ id: string }> {
    return this.http.post<{ id: string }>('/api/v1/admin/users', request);
  }

  updateUser(id: string, request: UpdateUserRequest): Observable<void> {
    return this.http.put<void>(`/api/v1/admin/users/${id}`, request);
  }

  deactivateUser(id: string): Observable<void> {
    return this.http.delete<void>(`/api/v1/admin/users/${id}`);
  }

  listLegalEntities(): Observable<LegalEntitySummary[]> {
    return this.http.get<LegalEntitySummary[]>('/api/v1/admin/users/legal-entities');
  }

  listRoles(): Observable<RoleDto[]> {
    return this.http.get<RoleDto[]>('/api/v1/admin/users/roles');
  }
}
