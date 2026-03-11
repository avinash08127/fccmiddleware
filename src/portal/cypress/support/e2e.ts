import './commands';

// Prevent uncaught exceptions from failing tests (Angular/MSAL may throw during test teardown)
Cypress.on('uncaught:exception', (err) => {
  // MSAL interaction_in_progress or BrowserAuthError during mock auth
  if (
    err.message.includes('interaction_in_progress') ||
    err.message.includes('BrowserAuthError') ||
    err.message.includes('no_account_error')
  ) {
    return false;
  }
  // Let other errors fail the test
  return true;
});
