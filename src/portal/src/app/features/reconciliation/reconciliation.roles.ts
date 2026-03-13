import { AppRole } from '../../core/auth/auth-state';

export const RECONCILIATION_VIEW_ROLES: AppRole[] = [
  'SystemAdmin',
  'SystemAdministrator',
  'OperationsManager',
  'SiteSupervisor',
  'Auditor',
  'SupportReadOnly',
];

export const RECONCILIATION_REVIEW_ROLES: AppRole[] = [
  'SystemAdmin',
  'SystemAdministrator',
  'OperationsManager',
];
