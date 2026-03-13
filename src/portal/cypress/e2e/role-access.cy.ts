describe('Role-Based Access Control', () => {
  beforeEach(() => {
    cy.interceptCommonApis();
    cy.interceptDashboardApis();
    cy.interceptReconciliationApis();
    cy.interceptSiteConfigApis();
  });

  // ─── OperationsManager ─────────────────────────────────────────────────────

  describe('OperationsManager role', () => {
    beforeEach(() => {
      cy.visit('/');
      cy.loginAs('OperationsManager');
    });

    it('should access dashboard', () => {
      cy.visit('/dashboard');
      cy.contains('h1', 'Dashboard').should('be.visible');
    });

    it('should access reconciliation and see approve/reject buttons', () => {
      cy.visit('/reconciliation/exceptions/recon-001');
      cy.wait('@getReconciliationDetail');

      cy.contains('Approve Variance').should('be.visible');
      cy.contains('Reject').should('be.visible');
    });

    it('should access site config and see Edit button', () => {
      cy.visit('/sites/site-001');
      cy.wait('@getSiteDetail');

      cy.contains('button', 'Edit').should('be.visible');
    });

    it('should see Settings in navigation', () => {
      cy.visit('/dashboard');
      cy.contains('Settings').should('exist');
    });
  });

  // ─── Auditor ───────────────────────────────────────────────────────────────

  describe('Auditor role', () => {
    beforeEach(() => {
      cy.visit('/');
      cy.loginAs('Auditor');
    });

    it('should access dashboard in read-only mode', () => {
      cy.visit('/dashboard');
      cy.contains('h1', 'Dashboard').should('be.visible');
    });

    it('should NOT see approve/reject buttons on reconciliation detail', () => {
      cy.visit('/reconciliation/exceptions/recon-001');
      cy.wait('@getReconciliationDetail');

      // Auditors can read reconciliation details but cannot review them.
      cy.contains('Approve Variance').should('not.exist');
      cy.contains('button', 'Reject').should('not.exist');
    });

    it('should NOT see Edit button on site config', () => {
      cy.visit('/sites/site-001');
      cy.wait('@getSiteDetail');

      // Auditors should not see edit controls
      cy.contains('button', 'Edit').should('not.exist');
    });
  });

  // ─── SiteSupervisor ────────────────────────────────────────────────────────

  describe('SiteSupervisor role', () => {
    beforeEach(() => {
      cy.visit('/');
      cy.loginAs('SiteSupervisor');
    });

    it('should access dashboard', () => {
      cy.visit('/dashboard');
      cy.contains('h1', 'Dashboard').should('be.visible');
    });

    it('should NOT see approve/reject buttons on reconciliation detail', () => {
      cy.visit('/reconciliation/exceptions/recon-001');
      cy.wait('@getReconciliationDetail');

      cy.contains('Approve Variance').should('not.exist');
      cy.contains('button', 'Reject').should('not.exist');
    });

    it('should NOT see Edit button on site config (view-only)', () => {
      cy.visit('/sites/site-001');
      cy.wait('@getSiteDetail');

      cy.contains('button', 'Edit').should('not.exist');
    });

    it('should see site detail information', () => {
      cy.visit('/sites/site-001');
      cy.wait('@getSiteDetail');

      // Can view site details
      cy.contains('Lilongwe Main').should('be.visible');
      cy.contains('Site Information').should('be.visible');
    });
  });

  // ─── SystemAdmin ───────────────────────────────────────────────────────────

  describe('SystemAdmin role', () => {
    beforeEach(() => {
      cy.visit('/');
      cy.loginAs('SystemAdmin');
    });

    it('should access dashboard', () => {
      cy.visit('/dashboard');
      cy.contains('h1', 'Dashboard').should('be.visible');
    });

    it('should see approve/reject buttons on reconciliation detail', () => {
      cy.visit('/reconciliation/exceptions/recon-001');
      cy.wait('@getReconciliationDetail');

      cy.contains('Approve Variance').should('be.visible');
      cy.contains('Reject').should('be.visible');
    });

    it('should see Edit button on site config', () => {
      cy.visit('/sites/site-001');
      cy.wait('@getSiteDetail');

      cy.contains('button', 'Edit').should('be.visible');
    });

    it('should access Settings page', () => {
      cy.visit('/settings');
      // SystemAdmin should be able to access settings
      cy.url().should('include', '/settings');
    });
  });

  // ─── SystemAdministrator ───────────────────────────────────────────────────

  describe('SystemAdministrator role', () => {
    beforeEach(() => {
      cy.visit('/');
      cy.loginAs('SystemAdministrator');
    });

    it('should access reconciliation and see approve/reject buttons', () => {
      cy.visit('/reconciliation/exceptions/recon-001');
      cy.wait('@getReconciliationDetail');

      cy.contains('Approve Variance').should('be.visible');
      cy.contains('Reject').should('be.visible');
    });
  });

  // ─── SupportReadOnly ───────────────────────────────────────────────────────

  describe('SupportReadOnly role', () => {
    beforeEach(() => {
      cy.visit('/');
      cy.loginAs('SupportReadOnly');
    });

    it('should access reconciliation detail in read-only mode', () => {
      cy.visit('/reconciliation/exceptions/recon-001');
      cy.wait('@getReconciliationDetail');

      cy.contains('Reconciliation #').should('be.visible');
      cy.contains('Approve Variance').should('not.exist');
      cy.contains('button', 'Reject').should('not.exist');
    });
  });
});
