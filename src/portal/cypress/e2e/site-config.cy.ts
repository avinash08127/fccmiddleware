describe('Site Configuration', () => {
  beforeEach(() => {
    cy.interceptCommonApis();
    cy.interceptSiteConfigApis();
    cy.visit('/');
    cy.loginAs('OperationsManager');
  });

  it('should display site detail page with all sections', () => {
    cy.visit('/sites/site-001');
    cy.wait('@getSiteDetail');

    // Title
    cy.contains('Lilongwe Main').should('be.visible');
    cy.contains('MW-001').should('be.visible');

    // Status badge
    cy.contains('Active').should('be.visible');

    // Site information section
    cy.contains('Site Information').should('be.visible');
    cy.contains('Site Code').should('be.visible');
    cy.contains('Site Name').should('be.visible');
    cy.contains('Timezone').should('be.visible');
    cy.contains('Operator').should('be.visible');
    cy.contains('Operating Model').should('be.visible');
    cy.contains('Connectivity Mode').should('be.visible');
  });

  it('should display FCC configuration section', () => {
    cy.visit('/sites/site-001');
    cy.wait('@getSiteDetail');

    // FCC config fields should be present
    cy.contains('DOMS').should('be.visible');
    cy.contains('192.168.1.100').should('be.visible');
  });

  it('should display pump mapping section', () => {
    cy.visit('/sites/site-001');
    cy.wait('@getSiteDetail');

    // Pump information from fixture
    cy.contains('PMS').should('be.visible');
    cy.contains('AGO').should('be.visible');
  });

  it('should display tolerance configuration section', () => {
    cy.visit('/sites/site-001');
    cy.wait('@getSiteDetail');

    cy.contains('Reconciliation Tolerances').should('be.visible');
    cy.contains('Amount Tolerance').should('be.visible');
    cy.contains('Time Window').should('be.visible');
  });

  it('should display fiscalization section', () => {
    cy.visit('/sites/site-001');
    cy.wait('@getSiteDetail');

    cy.contains('Fiscalization').should('be.visible');
    cy.contains('Fiscalization Mode').should('be.visible');
  });

  it('should show Edit button for authorized users', () => {
    cy.visit('/sites/site-001');
    cy.wait('@getSiteDetail');

    cy.contains('button', 'Edit').should('be.visible');
  });

  it('should enter edit mode and show Save/Cancel buttons', () => {
    cy.visit('/sites/site-001');
    cy.wait('@getSiteDetail');

    // Click edit
    cy.contains('button', 'Edit').click();

    // Should show Save and Cancel buttons
    cy.contains('button', 'Save Changes').should('be.visible');
    cy.contains('button', 'Cancel').should('be.visible');

    // Edit button should be gone
    cy.contains('button', 'Edit').should('not.exist');
  });

  it('should cancel edit mode without saving', () => {
    cy.visit('/sites/site-001');
    cy.wait('@getSiteDetail');

    cy.contains('button', 'Edit').click();
    cy.contains('button', 'Cancel').click();

    // Back to view mode
    cy.contains('button', 'Edit').should('be.visible');
    cy.contains('button', 'Save Changes').should('not.exist');
  });

  it('should save changes in edit mode', () => {
    cy.visit('/sites/site-001');
    cy.wait('@getSiteDetail');

    // Enter edit mode
    cy.contains('button', 'Edit').click();

    // Click save
    cy.contains('button', 'Save Changes').click();

    cy.wait('@updateSite');

    // Success toast
    cy.contains('Changes saved').should('be.visible');

    // Back to view mode
    cy.contains('button', 'Edit').should('be.visible');
  });

  it('should show back button that navigates to sites list', () => {
    cy.visit('/sites/site-001');
    cy.wait('@getSiteDetail');

    cy.contains('Back to Sites').should('be.visible');
    cy.contains('Back to Sites').click();
    cy.url().should('include', '/sites');
  });

  it('should display site info fields correctly', () => {
    cy.visit('/sites/site-001');
    cy.wait('@getSiteDetail');

    cy.contains('Africa/Blantyre').should('be.visible');
    cy.contains('Fuel Corp Ltd').should('be.visible');
    cy.contains('COCO').should('be.visible');
    cy.contains('CONNECTED').should('be.visible');
  });
});
