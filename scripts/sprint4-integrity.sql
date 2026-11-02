-- Run only against the explicitly isolated Sprint 4 fixture database.
SELECT 'campaign_bound_violations', count(*) FROM group_buy_campaigns WHERE "ReservedQuantity" < 0 OR "ReservedQuantity" > "TargetQuantity" OR "CommittedQuantity" < 0 OR "CommittedQuantity" + "FulfilledQuantity" > "TargetQuantity";
SELECT 'snapshot_violations', count(*) FROM group_buy_reservations WHERE "TotalPriceSnapshot" <> round("Quantity" * "UnitPriceSnapshot", 2);
SELECT 'collected_secret_violations', count(*) FROM pickup_records WHERE "Status" = 2 AND ("CodeHash" IS NOT NULL OR "CollectedAtUtc" IS NULL);
SELECT 'notification_duplicate_violations', count(*) FROM (SELECT "DeduplicationKey" FROM internal_notifications GROUP BY 1 HAVING count(*) > 1) d;
SELECT 'campaigns', count(*) FROM group_buy_campaigns;
SELECT 'reservations', count(*) FROM group_buy_reservations;
SELECT 'pickups', count(*) FROM pickup_records;
SELECT 'notifications', count(*) FROM internal_notifications;
SELECT 'audits', count(*) FROM audit_logs;
SELECT 'migration', "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 2;
