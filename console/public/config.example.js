// Copy to `console/public/config.js` (git-ignored) for local development, or
// serve an equivalent file next to `index.html` in a deployment.
//
// The credential is a bearer token of the public HIVE API. Its organization
// scope drives the organogram (US-F1-01-T07); a credential that also carries a
// person binding drives the inbox, where responses and approval decisions are
// emitted as the positions that person occupies (US-F1-02-T04, and
// `docs/configuration.md` for how to configure one). It is visible to anyone who
// can load the console, so it must be scoped to that one person and never
// committed.
window.__HIVE_CONSOLE_CONFIG__ = {
  // Empty means "the console's own origin", which the dev-server proxy serves.
  apiBaseUrl: '',
  organizationId: 'acme-delivery',
  token: 'replace-me',
  pollIntervalMs: 5000,
};
