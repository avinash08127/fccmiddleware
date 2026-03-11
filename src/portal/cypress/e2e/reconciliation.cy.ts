describe('Reconciliation Workbench', () => {
  beforeEach(() => {
    cy.interceptCommonApis();
    cy.interceptReconciliationApis();
    cy.visit('/');
    cy.loginAs('OperationsManager');
  });

  it('should show "Select a Legal Entity" prompt before selection', () => {
    cy.visit('/reconciliation/exceptions');

    cy.contains('h1', 'Reconciliation Workbench').should('be.visible');
    cy.contains('Select a Legal Entity').should('be.visible');
  });

  it('should load variance flagged exceptions after selecting entity', () => {
    cy.visit('/reconciliation/exceptions');
    cy.selectLegalEntity('Malawi');

    cy.wait('@getReconciliationExceptions');

    // Variance tab should be active by default
    cy.contains('Variance Flagged').should('be.visible');

    // Should display the flagged exceptions in the table
    cy.contains('MW-001').should('be.visible');
    cy.contains('MW-002').should('be.visible');
  });

  it('should display correct table columns for exceptions', () => {
    cy.visit('/reconciliation/exceptions');
    cy.selectLegalEntity('Malawi');
    cy.wait('@getReconciliationExceptions');

    const expectedHeaders = [
      'Site',
      'Pump',
      'Nozzle',
      'Authorised Amt',
      'Actual Amt',
      'Variance',
      'Var %',
      'Match Method',
      'Status',
      'Created At',
    ];

    expectedHeaders.forEach((header) => {
      cy.contains('th', header).should('exist');
    });
  });

  it('should show tabs for Variance Flagged, Unmatched, and Reviewed', () => {
    cy.visit('/reconciliation/exceptions');
    cy.selectLegalEntity('Malawi');

    cy.contains('Variance Flagged').should('be.visible');
    cy.contains('Unmatched').should('be.visible');
    cy.contains('Reviewed').should('be.visible');
  });

  it('should navigate to detail view when clicking a row', () => {
    cy.visit('/reconciliation/exceptions');
    cy.selectLegalEntity('Malawi');
    cy.wait('@getReconciliationExceptions');

    // Click the first exception row
    cy.get('.clickable-row').first().click();

    // Should navigate to detail
    cy.url().should('include', '/reconciliation/exceptions/recon-001');
    cy.wait('@getReconciliationDetail');

    // Detail page elements
    cy.contains('Reconciliation #').should('be.visible');
    cy.contains('Pre-Auth Details').should('be.visible');
    cy.contains('Transaction Details').should('be.visible');
    cy.contains('Variance Breakdown').should('be.visible');
  });

  it('should display approve and reject buttons for VARIANCE_FLAGGED record', () => {
    cy.visit('/reconciliation/exceptions/recon-001');
    cy.wait('@getReconciliationDetail');

    cy.contains('Review Action').should('be.visible');
    cy.contains('Approve Variance').should('be.visible');
    cy.contains('Reject').should('be.visible');
  });

  it('should open approve dialog and require minimum 10-char reason', () => {
    cy.visit('/reconciliation/exceptions/recon-001');
    cy.wait('@getReconciliationDetail');

    // Click approve
    cy.contains('Approve Variance').click();

    // Dialog should appear
    cy.contains('Approve Variance').should('be.visible');
    cy.contains('You are about to').should('be.visible');
    cy.get('#reason').should('be.visible');

    // Approve button in dialog should be disabled (no reason entered)
    cy.get('.p-dialog-footer').find('button').contains('Approve').should('be.disabled');

    // Type short reason — validation error
    cy.get('#reason').type('Too short');
    cy.contains('Reason must be at least 10 characters').should('be.visible');
    cy.get('.p-dialog-footer').find('button').contains('Approve').should('be.disabled');
  });

  it('should successfully approve a variance exception with valid reason', () => {
    cy.visit('/reconciliation/exceptions/recon-001');
    cy.wait('@getReconciliationDetail');

    cy.contains('Approve Variance').click();

    // Type valid reason (>= 10 characters)
    cy.get('#reason').type('Variance is within acceptable range for this site and pump configuration.');

    // Approve button should now be enabled
    cy.get('.p-dialog-footer').find('button').contains('Approve').should('not.be.disabled');

    // Submit
    cy.get('.p-dialog-footer').find('button').contains('Approve').click();

    cy.wait('@approveReconciliation');

    // Success toast should appear
    cy.contains('Record approved').should('be.visible');
  });

  it('should display pre-auth and transaction details on the detail page', () => {
    cy.visit('/reconciliation/exceptions/recon-001');
    cy.wait('@getReconciliationDetail');

    // Pre-auth details
    cy.contains('Odoo Order ID').should('be.visible');
    cy.contains('ORD-2026-0001').should('be.visible');
    cy.contains('MW-001').should('be.visible');

    // Transaction details
    cy.contains('Transaction ID').should('be.visible');
    cy.contains('tx-001').should('be.visible');
  });

  it('should navigate back to exceptions list', () => {
    cy.visit('/reconciliation/exceptions/recon-001');
    cy.wait('@getReconciliationDetail');

    cy.contains('Back to Exceptions').click();
    cy.url().should('include', '/reconciliation/exceptions');
  });
});
