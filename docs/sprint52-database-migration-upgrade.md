# Sprint 5.2 database migration and upgrade

## Migration path

The current migration chain has five migrations. The two latest migrations add product-variant seller ownership and enforce it on campaign and inventory references. The variant seller value is derived from the existing owning product. PostgreSQL FK creation intentionally fails if historical campaigns already refer across sellers; no row is deleted or reassigned to make such an upgrade pass.

The new disposable upgrade tests construct a database at `20261102012900_AdminMfaSecurity`, insert synthetic sellers, products, variants, inventory, campaigns, reservations, and an audit record, then apply the chain. A valid-data case asserts all records survive, seller ownership is backfilled, and direct cross-seller campaign/inventory updates are rejected. An invalid legacy campaign case expects SQLSTATE `23503`, confirms the new migration is not recorded, confirms the new column was rolled back, and asserts the original reference and audit row remain unchanged. Fresh migration is covered by the existing integration fixture and suite.

## Role and promotion policy

The runtime role remains restricted and cannot execute schema migrations. Apply reviewed migrations only with the separate migrator credential from a controlled release job; do not place that credential in the API or staging runtime configuration. Inspect the live migration history and validate data consistency on an approved staging copy before any production planning. The recovered database is not an approved target for these tests.

## Evidence status

- Expected behavior: valid rows survive; cross-seller constraints reject invalid relationships.
- Implemented: representative synthetic fixtures and transactional failure assertions.
- Verification: see Sprint 5.2 report for exact final migration test totals and exit codes.
- Blocked: recovered/production schema version, grants, migration locks, backups, and production data quality have not been inspected. Any existing invalid cross-seller rows require a data-owner decision and a row-level remediation plan before deployment.
