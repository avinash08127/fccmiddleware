/// <reference types="cypress" />

// ─── Role type ────────────────────────────────────────────────────────────────
export type AppRole = 'SystemAdmin' | 'OperationsManager' | 'SiteSupervisor' | 'Auditor';

// ─── MSAL session mock ────────────────────────────────────────────────────────
// Instead of going through the real Azure Entra login flow, we inject MSAL
// session data into sessionStorage before visiting the app. This makes Angular's
// MsalGuard see an authenticated user without any network round-trip.

function buildMsalSessionData(role: AppRole) {
  const clientId = '00000000-0000-0000-0000-000000000001';
  const tenantId = '00000000-0000-0000-0000-000000000099';
  const homeAccountId = `${tenantId}.${clientId}`;
  const environment = 'login.microsoftonline.com';
  const realm = tenantId;

  const now = Math.floor(Date.now() / 1000);
  const expiresOn = now + 3600;

  const account = {
    homeAccountId,
    environment,
    tenantId,
    username: `test.${role.toLowerCase()}@fccmiddleware.com`,
    localAccountId: tenantId,
    name: `Test ${role}`,
    authorityType: 'MSSTS',
    idTokenClaims: {
      aud: clientId,
      iss: `https://login.microsoftonline.com/${tenantId}/v2.0`,
      name: `Test ${role}`,
      preferred_username: `test.${role.toLowerCase()}@fccmiddleware.com`,
      roles: [role],
      sub: tenantId,
      tid: tenantId,
    },
  };

  const idToken = {
    credentialType: 'IdToken',
    homeAccountId,
    environment,
    clientId,
    realm,
    secret: 'mock-id-token-jwt',
  };

  const accessToken = {
    credentialType: 'AccessToken',
    homeAccountId,
    environment,
    clientId,
    realm,
    target: `api://${clientId}/.default openid profile`,
    cachedAt: `${now}`,
    expiresOn: `${expiresOn}`,
    secret: 'mock-access-token-jwt',
  };

  return { account, idToken, accessToken, homeAccountId, environment, clientId, realm };
}

Cypress.Commands.add('loginAs', (role: AppRole) => {
  const { account, idToken, accessToken, homeAccountId, environment, clientId, realm } =
    buildMsalSessionData(role);

  const accountKey = `${homeAccountId}-${environment}-${realm}`;
  const idTokenKey = `${homeAccountId}-${environment}-idtoken-${clientId}-${realm}-`;
  const accessTokenKey = `${homeAccountId}-${environment}-accesstoken-${clientId}-${realm}-${accessToken.target}`;

  cy.window().then((win) => {
    win.sessionStorage.setItem(accountKey, JSON.stringify(account));
    win.sessionStorage.setItem(idTokenKey, JSON.stringify(idToken));
    win.sessionStorage.setItem(accessTokenKey, JSON.stringify(accessToken));
    // MSAL active account key
    win.sessionStorage.setItem(
      `msal.${clientId}.active-account`,
      accountKey,
    );
  });
});

// ─── Common API intercepts ───────────────────────────────────────────────────

Cypress.Commands.add('interceptCommonApis', () => {
  // Legal entities (used by almost all pages)
  cy.intercept('GET', '**/api/v1/master-data/legal-entities', {
    fixture: 'legal-entities.json',
  }).as('getLegalEntities');
});

Cypress.Commands.add('interceptDashboardApis', () => {
  cy.intercept('GET', '**/api/v1/dashboard/summary*', {
    fixture: 'dashboard-summary.json',
  }).as('getDashboardSummary');

  cy.intercept('GET', '**/api/v1/dashboard/alerts*', {
    fixture: 'dashboard-alerts.json',
  }).as('getDashboardAlerts');
});

Cypress.Commands.add('interceptTransactionApis', () => {
  cy.intercept('GET', '**/api/v1/transactions?*', {
    fixture: 'transactions.json',
  }).as('getTransactions');

  cy.intercept('GET', '**/api/v1/transactions/tx-001', {
    fixture: 'transaction-detail.json',
  }).as('getTransactionDetail');

  cy.intercept('GET', '**/api/v1/audit-events*', {
    fixture: 'audit-events.json',
  }).as('getAuditEvents');

  cy.intercept('GET', '**/api/v1/sites?*', {
    fixture: 'sites.json',
  }).as('getSites');
});

Cypress.Commands.add('interceptReconciliationApis', () => {
  cy.intercept('GET', '**/api/v1/reconciliation/exceptions*', {
    fixture: 'reconciliation-exceptions.json',
  }).as('getReconciliationExceptions');

  cy.intercept('GET', '**/api/v1/reconciliation/exceptions/recon-001', {
    fixture: 'reconciliation-detail.json',
  }).as('getReconciliationDetail');

  cy.intercept('POST', '**/api/v1/reconciliation/exceptions/recon-001/approve', {
    fixture: 'reconciliation-approved.json',
  }).as('approveReconciliation');

  cy.intercept('POST', '**/api/v1/reconciliation/exceptions/recon-001/reject', {
    fixture: 'reconciliation-approved.json',
  }).as('rejectReconciliation');

  cy.intercept('GET', '**/api/v1/sites?*', {
    fixture: 'sites.json',
  }).as('getSitesRecon');
});

Cypress.Commands.add('interceptAgentApis', () => {
  cy.intercept('GET', '**/api/v1/agents?*', {
    fixture: 'agents.json',
  }).as('getAgents');

  cy.intercept('GET', '**/api/v1/agents/agent-001', (req) => {
    req.reply({ fixture: 'agent-detail.json', headers: { 'x-fixture': 'registration' } });
  }).as('getAgentRegistration');

  cy.intercept('GET', '**/api/v1/agents/agent-001/telemetry', (req) => {
    req.reply({ fixture: 'agent-detail.json', headers: { 'x-fixture': 'telemetry' } });
  }).as('getAgentTelemetry');

  cy.intercept('GET', '**/api/v1/agents/agent-001/events*', (req) => {
    req.reply({ fixture: 'agent-detail.json', headers: { 'x-fixture': 'events' } });
  }).as('getAgentEvents');
});

Cypress.Commands.add('interceptSiteConfigApis', () => {
  cy.intercept('GET', '**/api/v1/sites/site-001', {
    fixture: 'site-detail.json',
  }).as('getSiteDetail');

  cy.intercept('PUT', '**/api/v1/sites/site-001', {
    fixture: 'site-detail.json',
  }).as('updateSite');

  cy.intercept('PUT', '**/api/v1/sites/site-001/fcc-config', {
    fixture: 'site-detail.json',
  }).as('updateFccConfig');

  cy.intercept('GET', '**/api/v1/master-data/products*', {
    fixture: 'products.json',
  }).as('getProducts');
});

// ─── Select a legal entity from the PrimeNG p-select dropdown ────────────────

Cypress.Commands.add('selectLegalEntity', (entityName: string) => {
  cy.get('.entity-selector').click();
  cy.get('.p-select-overlay .p-select-option').contains(entityName).click();
});

// ─── Type declarations ──────────────────────────────────────────────────────

declare global {
  namespace Cypress {
    interface Chainable {
      loginAs(role: AppRole): Chainable<void>;
      interceptCommonApis(): Chainable<void>;
      interceptDashboardApis(): Chainable<void>;
      interceptTransactionApis(): Chainable<void>;
      interceptReconciliationApis(): Chainable<void>;
      interceptAgentApis(): Chainable<void>;
      interceptSiteConfigApis(): Chainable<void>;
      selectLegalEntity(entityName: string): Chainable<void>;
    }
  }
}

export {};
