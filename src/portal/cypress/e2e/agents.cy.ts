describe('Edge Agent Monitoring', () => {
  beforeEach(() => {
    cy.interceptCommonApis();
    cy.interceptAgentApis();
    cy.visit('/');
    cy.loginAs('OperationsManager');
  });

  it('should show "Select a Legal Entity" prompt before selection', () => {
    cy.visit('/agents');

    cy.contains('h1', 'Edge Agent Monitoring').should('be.visible');
    cy.contains('Select a Legal Entity').should('be.visible');
  });

  it('should load and display agents after selecting a legal entity', () => {
    cy.visit('/agents');
    cy.selectLegalEntity('Malawi');

    cy.wait('@getAgents');

    // Should display agents in the table
    cy.contains('MW-001').should('be.visible');
    cy.contains('MW-002').should('be.visible');
    cy.contains('Lilongwe Main').should('be.visible');
  });

  it('should display correct table columns', () => {
    cy.visit('/agents');
    cy.selectLegalEntity('Malawi');
    cy.wait('@getAgents');

    const expectedHeaders = [
      'Site',
      'Connectivity',
      'Buffer',
      'Last Seen',
      'Battery',
      'Version',
      'Sync Lag',
    ];

    expectedHeaders.forEach((header) => {
      cy.contains('th', header).should('exist');
    });
  });

  it('should show offline agents section for agents with FULLY_OFFLINE state', () => {
    cy.visit('/agents');
    cy.selectLegalEntity('Malawi');
    cy.wait('@getAgents');

    // MW-015 is offline in the fixture
    cy.contains('Offline').should('be.visible');
    cy.contains('MW-015').should('be.visible');
    cy.contains('Mzuzu Station').should('be.visible');
  });

  it('should display connectivity badges with correct styling', () => {
    cy.visit('/agents');
    cy.selectLegalEntity('Malawi');
    cy.wait('@getAgents');

    // Online agents should have the 'Online' badge
    cy.get('.badge-online').should('exist');
    // Offline agent should have the 'Offline' badge
    cy.get('.badge-offline').should('exist');
  });

  it('should display battery percentage', () => {
    cy.visit('/agents');
    cy.selectLegalEntity('Malawi');
    cy.wait('@getAgents');

    cy.contains('85%').should('be.visible');
    cy.contains('92%').should('be.visible');
    cy.contains('12%').should('be.visible');
  });

  it('should show agent version', () => {
    cy.visit('/agents');
    cy.selectLegalEntity('Malawi');
    cy.wait('@getAgents');

    cy.contains('2.1.0').should('be.visible');
    cy.contains('2.0.5').should('be.visible');
  });

  it('should show filter panel with site code and connectivity state filters', () => {
    cy.visit('/agents');
    cy.selectLegalEntity('Malawi');
    cy.wait('@getAgents');

    cy.contains('Filters').should('be.visible');
    cy.contains('Site Code').should('be.visible');
    cy.contains('Connectivity State').should('be.visible');
    cy.contains('Clear').should('be.visible');
  });

  it('should navigate to agent detail when clicking a row', () => {
    cy.visit('/agents');
    cy.selectLegalEntity('Malawi');
    cy.wait('@getAgents');

    // Click on MW-001 agent row (in online table)
    cy.get('.clickable-row').contains('MW-001').click();

    // Should navigate to detail page
    cy.url().should('include', '/agents/agent-001');
  });

  it('should display agent detail page with all cards', () => {
    cy.visit('/agents/agent-001');

    // Wait for all detail API calls
    cy.wait('@getAgentRegistration');

    // Page title should show site code
    cy.contains('MW-001').should('be.visible');

    // Detail cards
    cy.contains('Current Status').should('be.visible');
    cy.contains('FCC Connection').should('be.visible');
    cy.contains('Device & Buffer').should('be.visible');
    cy.contains('Sync Status').should('be.visible');

    // Connectivity timeline
    cy.contains('Connectivity Timeline').should('be.visible');

    // Recent events
    cy.contains('Recent Events').should('be.visible');
  });

  it('should display device info in the detail view', () => {
    cy.visit('/agents/agent-001');
    cy.wait('@getAgentRegistration');

    // Device info
    cy.contains('Android 14').should('be.visible');
    cy.contains('Samsung Galaxy Tab A8').should('be.visible');

    // FCC info
    cy.contains('DOMS').should('be.visible');
    cy.contains('192.168.1.100').should('be.visible');
  });

  it('should show back button that navigates to agent list', () => {
    cy.visit('/agents/agent-001');
    cy.wait('@getAgentRegistration');

    cy.get('[ptooltip="Back to agent list"]').should('be.visible');
  });
});
