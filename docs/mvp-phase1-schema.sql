START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    ALTER TABLE group_buy_campaigns ADD "ExtensionCount" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    ALTER TABLE group_buy_campaigns ADD "FinalQuantity" numeric(18,3);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    ALTER TABLE group_buy_campaigns ADD "FinalUnitPrice" numeric(18,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    ALTER TABLE group_buy_campaigns ADD "FinalizedAtUtc" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    ALTER TABLE group_buy_campaigns ADD "NearTargetAccepted" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    ALTER TABLE group_buy_campaigns ADD "NearTargetQuantity" numeric(18,3);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    ALTER TABLE group_buy_campaigns ADD "RescueNotifiedAtUtc" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    ALTER TABLE group_buy_campaigns ADD "ServiceArea" character varying(80) NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    ALTER TABLE group_buy_campaigns ADD "UsesTierPricing" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE TABLE campaign_price_tiers (
        "Id" uuid NOT NULL,
        "CampaignId" uuid NOT NULL,
        "Quantity" numeric(18,3) NOT NULL,
        "UnitPrice" numeric(18,2) NOT NULL,
        CONSTRAINT "PK_campaign_price_tiers" PRIMARY KEY ("Id"),
        CONSTRAINT ck_tier_values CHECK ("Quantity" > 0 AND "UnitPrice" > 0),
        CONSTRAINT "FK_campaign_price_tiers_group_buy_campaigns_CampaignId" FOREIGN KEY ("CampaignId") REFERENCES group_buy_campaigns ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE TABLE customer_interests (
        "Id" uuid NOT NULL,
        "CustomerId" uuid NOT NULL,
        "ProductId" uuid NOT NULL,
        "ServiceArea" character varying(80) NOT NULL,
        "DesiredQuantity" numeric(18,3) NOT NULL,
        "Active" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_customer_interests" PRIMARY KEY ("Id"),
        CONSTRAINT ck_interest_quantity CHECK ("DesiredQuantity" > 0 AND "DesiredQuantity" <= 1000000),
        CONSTRAINT "FK_customer_interests_products_ProductId" FOREIGN KEY ("ProductId") REFERENCES products ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_customer_interests_users_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES users ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE TABLE payment_orders (
        "Id" uuid NOT NULL,
        "CampaignId" uuid NOT NULL,
        "CustomerId" uuid NOT NULL,
        "ReservationId" uuid,
        "Quantity" numeric(18,3) NOT NULL,
        "MaximumUnitPrice" numeric(18,2) NOT NULL,
        "MaximumAmountMinor" bigint NOT NULL,
        "DeliveryFeeMinor" bigint NOT NULL,
        "FinalAmountMinor" bigint,
        "RefundedMinor" bigint NOT NULL,
        "State" integer NOT NULL,
        "IdempotencyKey" character varying(100) NOT NULL,
        "Provider" character varying(24) NOT NULL,
        "ProviderOrderId" character varying(100),
        "ProviderPaymentId" character varying(100),
        "ExpiresAtUtc" timestamp with time zone NOT NULL,
        "CapturedAtUtc" timestamp with time zone,
        "ReconciledAtUtc" timestamp with time zone,
        "Attempts" integer NOT NULL,
        "RetryAtUtc" timestamp with time zone,
        "ErrorCode" character varying(80),
        "FulfillmentMode" integer NOT NULL,
        "AddressCiphertext" character varying(12000) NOT NULL,
        "AddressDigest" character varying(64) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_payment_orders" PRIMARY KEY ("Id"),
        CONSTRAINT ck_payment_amounts CHECK ("Quantity" > 0 AND "MaximumUnitPrice" > 0 AND "MaximumAmountMinor" > 0 AND "DeliveryFeeMinor" >= 0 AND "RefundedMinor" BETWEEN 0 AND "MaximumAmountMinor" AND ("FinalAmountMinor" IS NULL OR "FinalAmountMinor" BETWEEN 0 AND "MaximumAmountMinor")),
        CONSTRAINT ck_payment_state CHECK ("State" BETWEEN 0 AND 7 AND "FulfillmentMode" BETWEEN 0 AND 1 AND "Attempts" >= 0),
        CONSTRAINT "FK_payment_orders_group_buy_campaigns_CampaignId" FOREIGN KEY ("CampaignId") REFERENCES group_buy_campaigns ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_payment_orders_group_buy_reservations_ReservationId" FOREIGN KEY ("ReservationId") REFERENCES group_buy_reservations ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_payment_orders_users_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES users ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE TABLE payment_webhooks (
        "Id" uuid NOT NULL,
        "Provider" character varying(24) NOT NULL,
        "EventId" character varying(120) NOT NULL,
        "PayloadHash" character varying(64) NOT NULL,
        "ProviderOrderId" character varying(100) NOT NULL,
        "ReceivedAtUtc" timestamp with time zone NOT NULL,
        "ProcessedAtUtc" timestamp with time zone,
        "Attempts" integer NOT NULL,
        "RetryAtUtc" timestamp with time zone,
        "ErrorCode" character varying(4000),
        CONSTRAINT "PK_payment_webhooks" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE TABLE sandbox_payment_receipts (
        "Id" uuid NOT NULL,
        "LocalOrderId" uuid NOT NULL,
        "ProviderOrderId" character varying(100) NOT NULL,
        "AmountMinor" bigint NOT NULL,
        "State" character varying(24) NOT NULL,
        "RefundedMinor" bigint NOT NULL,
        CONSTRAINT "PK_sandbox_payment_receipts" PRIMARY KEY ("Id"),
        CONSTRAINT "AK_sandbox_payment_receipts_LocalOrderId" UNIQUE ("LocalOrderId"),
        CONSTRAINT ck_sandbox_amount CHECK ("AmountMinor" > 0 AND "RefundedMinor" BETWEEN 0 AND "AmountMinor")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE TABLE order_fulfillments (
        "Id" uuid NOT NULL,
        "PaymentOrderId" uuid NOT NULL,
        "PickupId" uuid NOT NULL,
        "State" integer NOT NULL,
        "ChoiceConfirmed" boolean NOT NULL,
        "TokenHash" character varying(64),
        "BackupCodeHash" character varying(64),
        "SecretExpiresAtUtc" timestamp with time zone,
        "SecretIssuedAtUtc" timestamp with time zone,
        "FailedAttempts" integer NOT NULL,
        "LockedUntilUtc" timestamp with time zone,
        "VerifiedBy" uuid,
        "VerifiedUntilUtc" timestamp with time zone,
        "RedeemedAtUtc" timestamp with time zone,
        "CodeVersion" integer NOT NULL,
        "ExceptionReason" character varying(300),
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_order_fulfillments" PRIMARY KEY ("Id"),
        CONSTRAINT ck_fulfillment_values CHECK ("State" BETWEEN 0 AND 10 AND "FailedAttempts" BETWEEN 0 AND 5 AND "CodeVersion" >= 0),
        CONSTRAINT "FK_order_fulfillments_payment_orders_PaymentOrderId" FOREIGN KEY ("PaymentOrderId") REFERENCES payment_orders ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_order_fulfillments_pickup_records_PickupId" FOREIGN KEY ("PickupId") REFERENCES pickup_records ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_order_fulfillments_users_VerifiedBy" FOREIGN KEY ("VerifiedBy") REFERENCES users ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE TABLE payment_refunds (
        "Id" uuid NOT NULL,
        "PaymentOrderId" uuid NOT NULL,
        "Reason" character varying(60) NOT NULL,
        "AmountMinor" bigint NOT NULL,
        "State" integer NOT NULL,
        "ProviderRefundId" character varying(4000),
        "Attempts" integer NOT NULL,
        "RetryAtUtc" timestamp with time zone,
        "ErrorCode" character varying(80),
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_payment_refunds" PRIMARY KEY ("Id"),
        CONSTRAINT ck_refund_values CHECK ("AmountMinor" > 0 AND "Attempts" >= 0 AND "State" BETWEEN 0 AND 3),
        CONSTRAINT "FK_payment_refunds_payment_orders_PaymentOrderId" FOREIGN KEY ("PaymentOrderId") REFERENCES payment_orders ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE TABLE sandbox_refund_receipts (
        "Id" uuid NOT NULL,
        "RefundId" uuid NOT NULL,
        "LocalOrderId" uuid NOT NULL,
        "AmountMinor" bigint NOT NULL,
        CONSTRAINT "PK_sandbox_refund_receipts" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_sandbox_refund_receipts_sandbox_payment_receipts_LocalOrder~" FOREIGN KEY ("LocalOrderId") REFERENCES sandbox_payment_receipts ("LocalOrderId") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    ALTER TABLE group_buy_campaigns ADD CONSTRAINT ck_phase_one_campaign CHECK ("ExtensionCount" BETWEEN 0 AND 1 AND ("FinalUnitPrice" IS NULL OR "FinalUnitPrice" > 0));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_campaign_price_tiers_CampaignId_Quantity" ON campaign_price_tiers ("CampaignId", "Quantity");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_customer_interests_CustomerId_ProductId_ServiceArea" ON customer_interests ("CustomerId", "ProductId", "ServiceArea");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE INDEX "IX_customer_interests_ProductId" ON customer_interests ("ProductId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE INDEX "IX_customer_interests_ServiceArea_Active_UpdatedAt" ON customer_interests ("ServiceArea", "Active", "UpdatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_order_fulfillments_PaymentOrderId" ON order_fulfillments ("PaymentOrderId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_order_fulfillments_PickupId" ON order_fulfillments ("PickupId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE INDEX "IX_order_fulfillments_State_UpdatedAt" ON order_fulfillments ("State", "UpdatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_order_fulfillments_TokenHash" ON order_fulfillments ("TokenHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE INDEX "IX_order_fulfillments_VerifiedBy" ON order_fulfillments ("VerifiedBy");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE INDEX "IX_payment_orders_CampaignId_State" ON payment_orders ("CampaignId", "State");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_payment_orders_CustomerId_CampaignId_IdempotencyKey" ON payment_orders ("CustomerId", "CampaignId", "IdempotencyKey");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_payment_orders_Provider_ProviderOrderId" ON payment_orders ("Provider", "ProviderOrderId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_payment_orders_Provider_ProviderPaymentId" ON payment_orders ("Provider", "ProviderPaymentId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_payment_orders_ReservationId" ON payment_orders ("ReservationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE INDEX "IX_payment_orders_State_RetryAtUtc_ExpiresAtUtc" ON payment_orders ("State", "RetryAtUtc", "ExpiresAtUtc");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_payment_refunds_PaymentOrderId_Reason" ON payment_refunds ("PaymentOrderId", "Reason");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE INDEX "IX_payment_refunds_State_RetryAtUtc" ON payment_refunds ("State", "RetryAtUtc");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_payment_webhooks_Provider_EventId" ON payment_webhooks ("Provider", "EventId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_sandbox_payment_receipts_LocalOrderId" ON sandbox_payment_receipts ("LocalOrderId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_sandbox_payment_receipts_ProviderOrderId" ON sandbox_payment_receipts ("ProviderOrderId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE INDEX "IX_sandbox_refund_receipts_LocalOrderId" ON sandbox_refund_receipts ("LocalOrderId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    CREATE UNIQUE INDEX "IX_sandbox_refund_receipts_RefundId" ON sandbox_refund_receipts ("RefundId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104194747_PhaseOnePilotTransactions') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261104194747_PhaseOnePilotTransactions', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104225226_RecordFounderStockHandover') THEN
    ALTER TABLE order_fulfillments ADD "SellerHandedOverAtUtc" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104225226_RecordFounderStockHandover') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261104225226_RecordFounderStockHandover', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104233113_TrackPaidPickupPolicy') THEN
    ALTER TABLE pickup_records DROP CONSTRAINT ck_pickup_status;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104233113_TrackPaidPickupPolicy') THEN
    ALTER TABLE pickup_records ADD CONSTRAINT ck_pickup_status CHECK ("Status" BETWEEN 0 AND 4 AND "PaymentPolicy" IN (0, 1) AND "FailedAttempts" >= 0);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104233113_TrackPaidPickupPolicy') THEN
    UPDATE pickup_records AS p SET "PaymentPolicy" = 1
    FROM order_fulfillments AS f JOIN payment_orders AS o ON o."Id" = f."PaymentOrderId"
    WHERE f."PickupId" = p."Id" AND o."CapturedAtUtc" IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104233113_TrackPaidPickupPolicy') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261104233113_TrackPaidPickupPolicy', '10.0.14');
    END IF;
END $EF$;
COMMIT;

