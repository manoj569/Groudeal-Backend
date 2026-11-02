ANALYZE;
EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) SELECT * FROM group_buy_campaigns WHERE "Status" IN (2,3) ORDER BY "ExpiresAtUtc", "Id" LIMIT 20;
EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) SELECT * FROM group_buy_campaigns WHERE "Id" = (SELECT "Id" FROM group_buy_campaigns LIMIT 1);
EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) SELECT * FROM group_buy_reservations WHERE "CampaignId" = (SELECT "Id" FROM group_buy_campaigns LIMIT 1) AND "Status" = 0;
SELECT current_user, rolsuper, rolcreatedb FROM pg_roles WHERE rolname = current_user;
