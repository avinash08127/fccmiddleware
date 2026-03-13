import {
  getAccountRoles,
  getPrimaryRoleLabel,
  hasAnyRequiredRole,
  currentUserRole,
} from './auth-state';

describe('auth-state', () => {
  afterEach(() => {
    currentUserRole.set(null);
  });

  it('should return role from signal for getAccountRoles', () => {
    currentUserRole.set('FccAdmin');
    expect(getAccountRoles(null)).toEqual(['FccAdmin']);
  });

  it('should return empty array when no role is set', () => {
    expect(getAccountRoles(null)).toEqual([]);
  });

  it('should check required roles against signal', () => {
    currentUserRole.set('FccUser');
    expect(hasAnyRequiredRole(null, ['FccAdmin', 'FccUser'])).toBeTrue();
    expect(hasAnyRequiredRole(null, ['FccAdmin'])).toBeFalse();
  });

  it('should return primary role label from signal', () => {
    currentUserRole.set('FccViewer');
    expect(getPrimaryRoleLabel(null)).toBe('FccViewer');
  });
});
