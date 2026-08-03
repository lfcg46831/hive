// Copy to `console/public/config.js` (git-ignored) for local development, or
// serve an equivalent file next to `index.html` in a deployment.
//
// The credential is a read-only organization bearer token of the public HIVE
// API (US-F1-01-T07). It is visible to anyone who can load the console, so it
// must never be a token with write scope and never be committed.
window.__HIVE_CONSOLE_CONFIG__ = {
  // Empty means "the console's own origin", which the dev-server proxy serves.
  apiBaseUrl: '',
  organizationId: 'acme-delivery',
  token: 'replace-me',
  pollIntervalMs: 5000,
};
