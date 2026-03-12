import { getAccountRoles, getPrimaryRoleLabel, hasAnyRequiredRole } from './auth-state';

describe('auth-state', () => {
  function createAccount(roles: unknown) {
    return {
      idTokenClaims: {
        roles,
      },
    } as never;
  }

  it('should treat SystemAdministrator as equivalent to SystemAdmin', () => {
    const account = createAccount(['SystemAdministrator']);

    expect(getAccountRoles(account)).toEqual(['SystemAdministrator', 'SystemAdmin']);
    expect(hasAnyRequiredRole(account, ['SystemAdmin'])).toBeTrue();
  });

  it('should split comma-delimited role claims', () => {
    const account = createAccount('OperationsManager, SupportReadOnly');

    expect(getAccountRoles(account)).toEqual(['OperationsManager', 'SupportReadOnly']);
    expect(hasAnyRequiredRole(account, ['SupportReadOnly'])).toBeTrue();
  });

  it('should preserve the original primary role label for display', () => {
    const account = createAccount(['SystemAdministrator', 'Auditor']);

    expect(getPrimaryRoleLabel(account)).toBe('SystemAdministrator');
  });
});
