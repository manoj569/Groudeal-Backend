# Sprint 5.2 customer recovery and support gaps

## Existing behavior

- Registration, login, logout, current-account retrieval, lockout, account status/token-version revocation, and authenticated roles are implemented.
- Password authentication uses ASP.NET Core Identity password hashing and lockout. Admin accounts require MFA; recovery/reset operations are guarded operational workflows and audited.
- Reservations represent non-financial commitments. No endpoint claims that a reservation is paid. Cancellation, seller confirmation, campaign failure, pickup collection, and expiry follow persisted state transitions and audit records.
- Seller non-fulfillment and failed pickup are operational states with bounded worker retries; there is no automated refund, wallet, payment, escrow, delivery, or chargeback behavior.
- Administrative marketplace changes use authorization and audit records. The runtime DB role cannot rewrite/delete audit records.

## Open recovery and support requirements

- No self-service customer/seller forgot-password delivery flow or verified email ownership challenge is exposed. Implementing one requires a chosen email/SMS provider, abuse limits, token retention policy, privacy language, and frontend agreement.
- A customer account-access request, account deletion/export, identity verification, data retention, and support escalation policy require product/privacy/legal decisions. Existing `/me` scope is authenticated data access, not a complete privacy request workflow.
- Pickup disputes, seller non-fulfillment, cancelled campaigns, customer no-show, and seller suspension need an operational SLA and evidence workflow. No refund behavior should be inferred or introduced until payment rails and legal policy exist.
- Admin MFA reset/recovery requires a verified operator identity, dual control or documented incident process, and monitored administrative access. CLI audit actor fields are not human authentication.

## Pilot acceptance criteria

Product/support/privacy/legal owners must approve account recovery channels, verification and retention periods, support response targets, seller dispute outcomes, data access/deletion workflow, and admin break-glass control. Until then these items are **OPEN/BLOCKED**; no backend contract or marketplace business rule was changed in Sprint 5.2 to guess at policy.
