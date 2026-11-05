# Locked MVP Phase 1 implementation plan

Extend the existing modular monolith and React workspaces. Preserve Sprint 5.2 evidence and all legacy enum numeric values. No protected staging database receives migrations.

1. Add product/locality demand with owner-scoped idempotent interests and aggregate-only seller opportunities.
2. Add opt-in campaign tiers to existing campaigns; freeze prices after submission. Paid-only reservations use durable checkout holds and integer INR subunits. Legacy free reservations cannot join financial campaigns.
3. Implement Razorpay behind a provider boundary plus a durable, explicitly enabled non-production sandbox. Count captured funds only; authorized/pending/failed funds never count. Charge the disclosed maximum and refund tier reductions to the source payment. No wallet or escrow.
4. Persist webhook deduplication, refund operations and reconciliation/retry state. Provider calls occur outside business transactions. Unknown payment outcomes reconcile; outbound refunds reuse provider idempotency keys.
5. Finalize at the highest achieved tier at deadline, not necessarily maximum capacity. Lock seller/campaign for holds, callbacks, finalization and fulfillment; late funds are refunded rather than oversold.
6. Extend existing reservations/pickups with financial fulfillment, opaque Pickup Pass, six-digit backup/delivery OTP, explicit verification/handover, encrypted delivery addresses and founder-only delivery transitions.
7. Add customer/seller/Admin Phase 1 screens, clear sandbox labels, Razorpay checkout, rescue/sharing, demand, refunds, founder queue and metrics.
8. Generate additive EF migrations; test only on disposable PostgreSQL. Run backend/frontend suites, browser/mobile/PWA, lint/typecheck/build and document exact results.

Pilot activation inputs remain external: Razorpay credentials/webhook secret and merchant activation, approved real locality/postal codes, and an explicitly approved flat delivery fee. Sandbox defaults cannot activate production payments or delivery. Production readiness remains NO-GO.
