This is one normalized F0 demo example, not a required input schema. Real sources may submit free-form or partial `Directive.Context`; the triage prompt treats these keys as facts to look for.

title: Checkout returns HTTP 500 after payment confirmation
description: Customers complete payment successfully, then the checkout confirmation page fails before showing the order number. Support reports three affected customers in the last hour.
reported_severity: high
origin: support-desk
reproduction_steps:
  1. Add any in-stock product to the cart.
  2. Complete checkout with a test Visa card.
  3. Submit payment confirmation.
  4. Observe the confirmation page returning HTTP 500.
environment:
  product_area: web-checkout
  deployment: production
  version: 2026.07.04.3
  browser: Chrome 126
textual_attachments:
  - "support-log: order confirmation endpoint returned 500 for request req-7f3a"
  - "stack-trace-excerpt: NullReferenceException at CheckoutConfirmationPresenter.Build"
correlation_metadata:
  external_ticket: SUP-1842
  trace_id: req-7f3a
  reported_at_utc: 2026-07-04T15:20:00Z
