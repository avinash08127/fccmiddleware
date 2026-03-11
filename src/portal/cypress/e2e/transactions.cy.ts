describe('Transaction Browser', () => {
  beforeEach(() => {
    cy.interceptCommonApis();
    cy.interceptTransactionApis();
    cy.visit('/');
    cy.loginAs('OperationsManager');
  });

  it('should show "Select a Legal Entity" prompt before selection', () => {
    cy.visit('/transactions');

    cy.contains('h1', 'Transaction Browser').should('be.visible');
    cy.contains('Select a Legal Entity').should('be.visible');
  });

  it('should load and display transactions after selecting a legal entity', () => {
    cy.visit('/transactions');

    // Select legal entity
    cy.selectLegalEntity('Malawi');

    // Wait for transactions to load
    cy.wait('@getTransactions');

    // Table should show transaction data
    cy.contains('DOMS-20260311-0001').should('be.visible');
    cy.contains('DOMS-20260311-0002').should('be.visible');
    cy.contains('MW-001').should('be.visible');
    cy.contains('MW-002').should('be.visible');
  });

  it('should display transaction table headers correctly', () => {
    cy.visit('/transactions');
    cy.selectLegalEntity('Malawi');
    cy.wait('@getTransactions');

    const expectedHeaders = [
      'Transaction ID',
      'Site',
      'Pump',
      'Product',
      'Volume',
      'Amount',
      'Status',
      'Started At',
      'Source',
    ];

    expectedHeaders.forEach((header) => {
      cy.contains('th', header).should('be.visible');
    });
  });

  it('should show status badges with correct labels', () => {
    cy.visit('/transactions');
    cy.selectLegalEntity('Malawi');
    cy.wait('@getTransactions');

    // Transaction statuses should be visible as badges
    cy.get('app-status-badge').should('have.length.at.least', 1);
  });

  it('should navigate to transaction detail when clicking a row', () => {
    cy.visit('/transactions');
    cy.selectLegalEntity('Malawi');
    cy.wait('@getTransactions');

    // Click on the first transaction row
    cy.contains('DOMS-20260311-0001').click();

    // Should navigate to detail view
    cy.url().should('include', '/transactions/tx-001');

    // Wait for detail API
    cy.wait('@getTransactionDetail');

    // Detail page should show transaction info
    cy.contains('DOMS-20260311-0001').should('be.visible');
    cy.contains('Back to Transactions').should('be.visible');
  });

  it('should display transaction detail sections', () => {
    cy.visit('/transactions/tx-001');

    cy.wait('@getTransactionDetail');

    // Identifier section
    cy.contains('Identifiers').should('be.visible');
    cy.contains('FCC Transaction ID').should('be.visible');
    cy.contains('Correlation ID').should('be.visible');

    // Fuel dispensing section
    cy.contains('Fuel Dispensing').should('be.visible');
    cy.contains('Product').should('be.visible');
    cy.contains('Volume').should('be.visible');
    cy.contains('Amount').should('be.visible');

    // Status section
    cy.contains('Status').should('be.visible');
    cy.contains('Ingestion Source').should('be.visible');

    // Timestamps section
    cy.contains('Timestamps').should('be.visible');
    cy.contains('Started At').should('be.visible');

    // Event Trail card
    cy.contains('Event Trail').should('be.visible');
  });

  it('should display raw FCC payload panel (collapsed)', () => {
    cy.visit('/transactions/tx-001');
    cy.wait('@getTransactionDetail');

    cy.contains('Raw FCC Payload').should('be.visible');
  });

  it('should navigate back from detail to list', () => {
    cy.visit('/transactions/tx-001');
    cy.wait('@getTransactionDetail');

    cy.contains('Back to Transactions').click();
    cy.url().should('include', '/transactions');
  });
});
