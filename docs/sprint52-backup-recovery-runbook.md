# Sprint 5.2 PostgreSQL backup and recovery runbook

## Objectives and current status

Proposed initial objectives, pending business approval: **RPO 15 minutes** using continuous WAL archiving/PITR and **RTO 4 hours** for restoration of the production database and application encryption keys. A logical backup alone does not provide a 15-minute RPO. Production-grade RPO/RTO are **not verified** until an offsite archive, WAL retention, KMS/key recovery, and timed restore drill are configured.

The added regression test creates synthetic source and restore databases inside the disposable Testcontainers PostgreSQL instance, uses PostgreSQL `pg_dump` custom format, verifies the archive with `pg_restore --list`, restores into a different synthetic database, and compares exact synthetic row contents. The test compiled but was not executed in Sprint 5.2 because Docker inventory access was denied. It never reads or targets the recovered database.

## Production procedure to implement with the infrastructure owner

1. Run `pg_dump --format=custom --no-owner --no-privileges` using the dedicated backup identity and a consistent PostgreSQL snapshot. For large systems, use a tested base backup plus WAL archiving for PITR.
2. Stream the archive directly to an approved encrypted object-storage destination with server-side encryption/KMS. Do not leave plaintext dumps on a shared disk. Restrict write/read/delete roles separately, enable object versioning and immutable retention, and alert on missing scheduled artifacts.
3. Record object version, byte length, timestamp, database/schema version, and SHA-256 in a signed, access-controlled manifest. Verify `pg_restore --list` and checksum after upload.
4. Retain daily backups at least 35 days and monthly recovery points at least 12 months, subject to approved legal/data-retention policy. Test policy and object lock before enabling it.
5. Restore only to a newly provisioned isolated database with a distinct name and credentials. Apply/validate schema, migration history, representative row counts and application invariants, then run health/security checks. Do not restore over the source or recovered database.
6. Restore the Data Protection key ring and certificate/password through the organization's KMS/secrets manager before starting the application. Verify an existing synthetic protected value can be unprotected. If key material is lost, encrypted MFA/recovery fields and cookies may be unrecoverable; rotate signing keys and require appropriate re-enrollment only under an approved incident procedure.
7. Record elapsed restore time, recovery point age, integrity results, operator identities, key restore result, and cleanup of the disposable target.

## Key rotation / key-loss handling

Keep multiple active Data Protection keys until all protected application values have been reprotected or their retention window has passed. Back up the protected key ring and certificate through separate encrypted channels. Rotate JWT signing keys with a bounded overlap and invalidate affected sessions when compromise requires it. Losing both the key ring and its protecting certificate/password is a data-recovery incident, not a routine password reset.

## Status

- Implemented but not verified: disposable logical backup/archive integrity/restore test.
- Blocked: production KMS, offsite storage, WAL/PITR, immutable retention, ACLs, scheduled backup alerts, and real key restoration are not configured or exercised here.
- Manual action: obtain approved RPO/RTO, retention/privacy sign-off, assign backup owner, and complete a timed offsite restore before a pilot.
