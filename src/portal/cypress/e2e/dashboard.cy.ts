describe('Dashboard', () => {
  beforeEach(() => {
    cy.interceptCommonApis();
    cy.interceptDashboardApis();
  });

  it('should redirect to /dashboard after login and display all widgets', () => {
    cy.visit('/');
    cy.loginAs('OperationsManager');
    cy.visit('/dashboard');

    // Page title visible
    cy.contains('h1', 'Dashboard').should('be.visible');

    // Dashboard toolbar elements
    cy.get('.dashboard-toolbar').should('be.visible');
    cy.contains('Refresh').should('be.visible');

    // All 6 widget areas should render
    cy.get('app-transaction-volume-chart').should('exist');
    cy.get('app-ingestion-health').should('exist');
    cy.get('app-agent-status-summary').should('exist');
    cy.get('app-reconciliation-summary').should('exist');
    cy.get('app-stale-transactions').should('exist');
    cy.get('app-active-alerts').should('exist');
  });

  it('should display legal entity selector in the toolbar', () => {
    cy.visit('/');
    cy.loginAs('OperationsManager');
    cy.visit('/dashboard');

    cy.get('.entity-selector').should('be.visible');
  });

  it('should show refresh button and last updated timestamp', () => {
    cy.visit('/');
    cy.loginAs('OperationsManager');
    cy.visit('/dashboard');

    cy.contains('Refresh').should('be.visible');
    // After data loads, last refreshed should appear
    cy.get('.last-refreshed').should('exist');
  });

  it('should display navigation sidebar with all feature links', () => {
    cy.visit('/');
    cy.loginAs('OperationsManager');
    cy.visit('/dashboard');

    const navLabels = [
      'Dashboard',
      'Transactions',
      'Reconciliation',
      'Edge Agents',
      'Sites',
      'Master Data',
      'Audit Log',
      'Dead-Letter Queue',
      'Settings',
    ];

    navLabels.forEach((label) => {
      cy.contains(label).should('exist');
    });
  });
});
