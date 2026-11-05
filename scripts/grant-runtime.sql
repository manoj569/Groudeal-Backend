-- REVIEWED OPERATOR TEMPLATE ONLY. Dedicated Groudeal database; never run automatically.
-- Existing standalone non-owner LOGIN role must be created/credentialed by the secret manager.
-- Usage: psql ... -v app_role=<runtime-role-name> -v ON_ERROR_STOP=1 -f scripts/grant-runtime.sql
SELECT set_config('groudeal.app_role', :'app_role', false);
BEGIN;
CREATE OR REPLACE FUNCTION public.groudeal_guard_admin_security() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $guard$
BEGIN
 IF current_user = TG_ARGV[0] AND EXISTS (SELECT 1 FROM public.identity_user_roles WHERE "UserId" = OLD."Id" AND "RoleId" = '00000000-0000-0000-0000-000000000003'::uuid)
 AND ((OLD."TwoFactorEnabled" AND NOT NEW."TwoFactorEnabled") OR
      (NEW."MfaEnrollmentAllowedUntilUtc" IS NOT NULL AND (OLD."MfaEnrollmentAllowedUntilUtc" IS NULL OR NEW."MfaEnrollmentAllowedUntilUtc" > OLD."MfaEnrollmentAllowedUntilUtc"))) THEN
  RAISE EXCEPTION 'Runtime credentials cannot reset Admin MFA or authorize enrollment' USING ERRCODE = '42501';
 END IF;
 RETURN NEW;
END $guard$;
DO $policy$
DECLARE runtime_role text := current_setting('groudeal.app_role'); runtime_oid oid;
BEGIN
 SELECT oid INTO runtime_oid FROM pg_roles WHERE rolname = runtime_role AND rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolbypassrls;
 IF runtime_oid IS NULL OR runtime_role = current_user OR EXISTS (SELECT 1 FROM pg_auth_members WHERE member = runtime_oid)
 OR EXISTS (SELECT 1 FROM pg_class WHERE relowner = runtime_oid AND relnamespace = 'public'::regnamespace)
 OR EXISTS (SELECT 1 FROM pg_database WHERE datname = current_database() AND datdba = runtime_oid) THEN
  RAISE EXCEPTION 'Runtime identity must be a standalone non-owner login without elevated privileges';
 END IF;
 EXECUTE format('REVOKE ALL ON DATABASE %I FROM %I', current_database(), runtime_role);
 EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), runtime_role);
 REVOKE CREATE ON SCHEMA public FROM PUBLIC;
 REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
 EXECUTE format('REVOKE TEMPORARY ON DATABASE %I FROM PUBLIC', current_database());
 EXECUTE format('REVOKE ALL ON SCHEMA public FROM %I', runtime_role);
 EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', runtime_role);
 EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA public FROM %I', runtime_role);
 EXECUTE format('GRANT SELECT ON ALL TABLES IN SCHEMA public TO %I', runtime_role);
 EXECUTE format('GRANT INSERT ON users, audit_logs TO %I', runtime_role);
 EXECUTE format('GRANT INSERT, UPDATE ON identity_accounts, identity_user_tokens, sellers, seller_locations, products, product_variants, group_buy_campaigns, group_buy_reservations, inventory, pickup_records, internal_notifications, admin_mfa_challenges, admin_mfa_accepted_codes TO %I', runtime_role);
 EXECUTE format('GRANT INSERT, UPDATE ON customer_interests, campaign_price_tiers, payment_orders, payment_webhooks, payment_refunds, order_fulfillments, sandbox_payment_receipts, sandbox_refund_receipts, pilot_jobs TO %I', runtime_role);
 -- Draft-only tier replacement is the sole new delete operation; financial/audit rows remain durable.
 EXECUTE format('GRANT DELETE ON campaign_price_tiers TO %I', runtime_role);
 EXECUTE format('GRANT INSERT, DELETE ON identity_user_roles TO %I', runtime_role);
 ALTER TABLE public.identity_user_roles ENABLE ROW LEVEL SECURITY;
 DROP POLICY IF EXISTS groudeal_runtime_select ON public.identity_user_roles;
 DROP POLICY IF EXISTS groudeal_runtime_insert ON public.identity_user_roles;
 DROP POLICY IF EXISTS groudeal_runtime_delete ON public.identity_user_roles;
 EXECUTE format('CREATE POLICY groudeal_runtime_select ON public.identity_user_roles FOR SELECT TO %I USING (true)', runtime_role);
 EXECUTE format('CREATE POLICY groudeal_runtime_insert ON public.identity_user_roles FOR INSERT TO %I WITH CHECK ("RoleId" <> ''00000000-0000-0000-0000-000000000003''::uuid)', runtime_role);
 EXECUTE format('CREATE POLICY groudeal_runtime_delete ON public.identity_user_roles FOR DELETE TO %I USING ("RoleId" <> ''00000000-0000-0000-0000-000000000003''::uuid)', runtime_role);
 DROP TRIGGER IF EXISTS groudeal_runtime_admin_security ON public.identity_accounts;
 EXECUTE format('CREATE TRIGGER groudeal_runtime_admin_security BEFORE UPDATE ON public.identity_accounts FOR EACH ROW EXECUTE FUNCTION public.groudeal_guard_admin_security(%L)', runtime_role);
END $policy$;
COMMIT;
-- Review/reapply after migrations. Migration/provisioning credentials remain separate and unmounted on the API.

