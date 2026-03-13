import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { SelectModule } from 'primeng/select';
import { InputTextModule } from 'primeng/inputtext';
import { MultiSelectModule } from 'primeng/multiselect';
import { TagModule } from 'primeng/tag';
import { CheckboxModule } from 'primeng/checkbox';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService, ConfirmationService } from 'primeng/api';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import {
  UserManagementService,
  PortalUserDto,
  LegalEntitySummary,
  CreateUserRequest,
  UpdateUserRequest,
} from './user-management.service';

interface RoleOption {
  label: string;
  value: string;
}

@Component({
  selector: 'app-user-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    TableModule,
    ButtonModule,
    DialogModule,
    SelectModule,
    InputTextModule,
    MultiSelectModule,
    TagModule,
    CheckboxModule,
    TooltipModule,
    ToastModule,
    ConfirmDialogModule,
  ],
  providers: [MessageService, ConfirmationService],
  template: `
    <p-toast />
    <p-confirmDialog />

    <div class="user-management">
      <div class="user-management__header">
        <h2>User Management</h2>
        <p-button
          label="Add User"
          icon="pi pi-plus"
          (onClick)="openCreateDialog()"
        />
      </div>

      <!-- Filters -->
      <div class="user-management__filters">
        <span class="p-input-icon-left">
          <i class="pi pi-search"></i>
          <input
            pInputText
            type="text"
            placeholder="Search by name or email"
            [(ngModel)]="searchTerm"
            (input)="onSearch()"
          />
        </span>
        <p-select
          [options]="roleFilterOptions"
          [(ngModel)]="selectedRoleFilter"
          placeholder="All Roles"
          [showClear]="true"
          (onChange)="loadUsers()"
          styleClass="user-management__filter-dropdown"
        />
      </div>

      <!-- Users table -->
      <p-table
        [value]="users()"
        [paginator]="true"
        [rows]="pageSize"
        [totalRecords]="totalCount()"
        [lazy]="true"
        (onLazyLoad)="onPageChange($event)"
        [rowsPerPageOptions]="[10, 25, 50]"
        styleClass="p-datatable-sm"
      >
        <ng-template pTemplate="header">
          <tr>
            <th>Name</th>
            <th>Email</th>
            <th>Role</th>
            <th>Legal Entities</th>
            <th>Status</th>
            <th>Last Updated</th>
            <th style="width: 120px">Actions</th>
          </tr>
        </ng-template>
        <ng-template pTemplate="body" let-user>
          <tr>
            <td>{{ user.displayName }}</td>
            <td>{{ user.email }}</td>
            <td>
              <p-tag
                [value]="user.role"
                [severity]="getRoleSeverity(user.role)"
              />
            </td>
            <td>
              @if (user.allLegalEntities) {
                <p-tag value="All" severity="info" />
              } @else {
                <span class="user-management__le-list">
                  @for (le of user.legalEntities; track le.id) {
                    <p-tag [value]="le.countryCode + ' - ' + le.name" severity="secondary" />
                  }
                </span>
              }
            </td>
            <td>
              <p-tag
                [value]="user.isActive ? 'Active' : 'Inactive'"
                [severity]="user.isActive ? 'success' : 'danger'"
              />
            </td>
            <td>{{ user.updatedAt | date: 'short' }}</td>
            <td>
              <p-button
                icon="pi pi-pencil"
                [text]="true"
                severity="info"
                pTooltip="Edit"
                (onClick)="openEditDialog(user)"
              />
              @if (user.isActive) {
                <p-button
                  icon="pi pi-ban"
                  [text]="true"
                  severity="danger"
                  pTooltip="Deactivate"
                  (onClick)="confirmDeactivate(user)"
                />
              } @else {
                <p-button
                  icon="pi pi-check-circle"
                  [text]="true"
                  severity="success"
                  pTooltip="Reactivate"
                  (onClick)="reactivateUser(user)"
                />
              }
            </td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage">
          <tr>
            <td colspan="7" class="text-center">No users found.</td>
          </tr>
        </ng-template>
      </p-table>
    </div>

    <!-- Create / Edit Dialog -->
    <p-dialog
      [header]="dialogMode === 'create' ? 'Add User' : 'Edit User'"
      [(visible)]="dialogVisible"
      [modal]="true"
      [style]="{ width: '500px' }"
    >
      <div class="user-dialog">
        @if (dialogMode === 'create') {
          <div class="user-dialog__field">
            <label for="email">Email (Entra identity)</label>
            <input
              pInputText
              id="email"
              [(ngModel)]="formEmail"
              placeholder="user@company.com"
            />
          </div>
          <div class="user-dialog__field">
            <label for="displayName">Display Name</label>
            <input
              pInputText
              id="displayName"
              [(ngModel)]="formDisplayName"
              placeholder="Full Name"
            />
          </div>
        }

        <div class="user-dialog__field">
          <label for="role">Role</label>
          <p-select
            id="role"
            [options]="roleOptions"
            [(ngModel)]="formRole"
            placeholder="Select a role"
          />
        </div>

        <div class="user-dialog__field">
          <div class="user-dialog__checkbox-row">
            <p-checkbox
              [(ngModel)]="formAllLegalEntities"
              [binary]="true"
              inputId="allLE"
            />
            <label for="allLE">Access all legal entities</label>
          </div>
        </div>

        @if (!formAllLegalEntities) {
          <div class="user-dialog__field">
            <label for="legalEntities">Legal Entities</label>
            <p-multiSelect
              id="legalEntities"
              [options]="legalEntityOptions()"
              [(ngModel)]="formLegalEntityIds"
              optionLabel="label"
              optionValue="value"
              placeholder="Select legal entities"
              [filter]="true"
              display="chip"
            />
          </div>
        }
      </div>

      <ng-template pTemplate="footer">
        <p-button label="Cancel" severity="secondary" [text]="true" (onClick)="dialogVisible = false" />
        <p-button
          [label]="dialogMode === 'create' ? 'Create' : 'Save'"
          (onClick)="saveUser()"
          [loading]="saving()"
        />
      </ng-template>
    </p-dialog>
  `,
  styles: [
    `
      .user-management {
        padding: 1.5rem;
      }
      .user-management__header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        margin-bottom: 1rem;
      }
      .user-management__header h2 {
        margin: 0;
      }
      .user-management__filters {
        display: flex;
        gap: 1rem;
        margin-bottom: 1rem;
      }
      .user-management__filter-dropdown {
        min-width: 180px;
      }
      .user-management__le-list {
        display: flex;
        flex-wrap: wrap;
        gap: 0.25rem;
      }
      .user-dialog {
        display: flex;
        flex-direction: column;
        gap: 1rem;
      }
      .user-dialog__field {
        display: flex;
        flex-direction: column;
        gap: 0.25rem;
      }
      .user-dialog__field label {
        font-weight: 600;
        font-size: 0.875rem;
      }
      .user-dialog__checkbox-row {
        display: flex;
        align-items: center;
        gap: 0.5rem;
      }
    `,
  ],
})
export class UserListComponent implements OnInit {
  private readonly userService = inject(UserManagementService);
  private readonly messageService = inject(MessageService);
  private readonly confirmService = inject(ConfirmationService);

  users = signal<PortalUserDto[]>([]);
  totalCount = signal(0);
  page = 1;
  pageSize = 25;
  searchTerm = '';
  selectedRoleFilter: string | null = null;

  legalEntityOptions = signal<Array<{ label: string; value: string }>>([]);

  roleOptions: RoleOption[] = [
    { label: 'FCC Admin', value: 'FccAdmin' },
    { label: 'FCC User', value: 'FccUser' },
    { label: 'FCC Viewer', value: 'FccViewer' },
  ];

  roleFilterOptions: RoleOption[] = [
    { label: 'FCC Admin', value: 'FccAdmin' },
    { label: 'FCC User', value: 'FccUser' },
    { label: 'FCC Viewer', value: 'FccViewer' },
  ];

  // Dialog state
  dialogVisible = false;
  dialogMode: 'create' | 'edit' = 'create';
  editingUserId: string | null = null;
  formEmail = '';
  formDisplayName = '';
  formRole = '';
  formAllLegalEntities = false;
  formLegalEntityIds: string[] = [];
  saving = signal(false);

  private searchTimeout: ReturnType<typeof setTimeout> | null = null;

  ngOnInit(): void {
    this.loadUsers();
    this.loadLegalEntities();
  }

  loadUsers(): void {
    this.userService
      .listUsers(this.page, this.pageSize, {
        role: this.selectedRoleFilter ?? undefined,
        search: this.searchTerm || undefined,
      })
      .subscribe({
        next: (res) => {
          this.users.set(res.items);
          this.totalCount.set(res.totalCount);
        },
        error: () => {
          this.messageService.add({
            severity: 'error',
            summary: 'Error',
            detail: 'Failed to load users.',
          });
        },
      });
  }

  onSearch(): void {
    if (this.searchTimeout) clearTimeout(this.searchTimeout);
    this.searchTimeout = setTimeout(() => {
      this.page = 1;
      this.loadUsers();
    }, 300);
  }

  onPageChange(event: { first?: number | null; rows?: number | null }): void {
    const first = event.first ?? 0;
    const rows = event.rows ?? this.pageSize;
    this.page = Math.floor(first / rows) + 1;
    this.pageSize = rows;
    this.loadUsers();
  }

  getRoleSeverity(role: string): 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast' | undefined {
    switch (role) {
      case 'FccAdmin':
        return 'danger';
      case 'FccUser':
        return 'info';
      case 'FccViewer':
        return 'secondary';
      default:
        return undefined;
    }
  }

  openCreateDialog(): void {
    this.dialogMode = 'create';
    this.editingUserId = null;
    this.formEmail = '';
    this.formDisplayName = '';
    this.formRole = '';
    this.formAllLegalEntities = false;
    this.formLegalEntityIds = [];
    this.dialogVisible = true;
  }

  openEditDialog(user: PortalUserDto): void {
    this.dialogMode = 'edit';
    this.editingUserId = user.id;
    this.formEmail = user.email;
    this.formDisplayName = user.displayName;
    this.formRole = user.role;
    this.formAllLegalEntities = user.allLegalEntities;
    this.formLegalEntityIds = user.legalEntities.map((le) => le.id);
    this.dialogVisible = true;
  }

  saveUser(): void {
    if (this.dialogMode === 'create') {
      this.createUser();
    } else {
      this.updateUser();
    }
  }

  private createUser(): void {
    if (!this.formEmail || !this.formRole) {
      this.messageService.add({
        severity: 'warn',
        summary: 'Validation',
        detail: 'Email and Role are required.',
      });
      return;
    }

    this.saving.set(true);
    const request: CreateUserRequest = {
      email: this.formEmail,
      displayName: this.formDisplayName || this.formEmail,
      role: this.formRole,
      legalEntityIds: this.formLegalEntityIds,
      allLegalEntities: this.formAllLegalEntities,
    };

    this.userService.createUser(request).subscribe({
      next: () => {
        this.messageService.add({
          severity: 'success',
          summary: 'Success',
          detail: 'User created successfully.',
        });
        this.dialogVisible = false;
        this.saving.set(false);
        this.loadUsers();
      },
      error: (err) => {
        this.saving.set(false);
        const msg = err?.error?.message ?? 'Failed to create user.';
        this.messageService.add({ severity: 'error', summary: 'Error', detail: msg });
      },
    });
  }

  private updateUser(): void {
    if (!this.editingUserId) return;

    this.saving.set(true);
    const request: UpdateUserRequest = {
      role: this.formRole,
      legalEntityIds: this.formLegalEntityIds,
      allLegalEntities: this.formAllLegalEntities,
    };

    this.userService.updateUser(this.editingUserId, request).subscribe({
      next: () => {
        this.messageService.add({
          severity: 'success',
          summary: 'Success',
          detail: 'User updated successfully.',
        });
        this.dialogVisible = false;
        this.saving.set(false);
        this.loadUsers();
      },
      error: (err) => {
        this.saving.set(false);
        const msg = err?.error?.message ?? 'Failed to update user.';
        this.messageService.add({ severity: 'error', summary: 'Error', detail: msg });
      },
    });
  }

  confirmDeactivate(user: PortalUserDto): void {
    this.confirmService.confirm({
      message: `Deactivate ${user.displayName}? They will no longer be able to access the portal.`,
      header: 'Confirm Deactivation',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      accept: () => {
        this.userService.deactivateUser(user.id).subscribe({
          next: () => {
            this.messageService.add({
              severity: 'success',
              summary: 'Success',
              detail: 'User deactivated.',
            });
            this.loadUsers();
          },
          error: () => {
            this.messageService.add({
              severity: 'error',
              summary: 'Error',
              detail: 'Failed to deactivate user.',
            });
          },
        });
      },
    });
  }

  reactivateUser(user: PortalUserDto): void {
    this.userService.updateUser(user.id, { isActive: true }).subscribe({
      next: () => {
        this.messageService.add({
          severity: 'success',
          summary: 'Success',
          detail: 'User reactivated.',
        });
        this.loadUsers();
      },
      error: () => {
        this.messageService.add({
          severity: 'error',
          summary: 'Error',
          detail: 'Failed to reactivate user.',
        });
      },
    });
  }

  private loadLegalEntities(): void {
    this.userService.listLegalEntities().subscribe({
      next: (entities) => {
        this.legalEntityOptions.set(
          entities.map((le) => ({
            label: `${le.countryCode} - ${le.name}`,
            value: le.id,
          })),
        );
      },
    });
  }
}
