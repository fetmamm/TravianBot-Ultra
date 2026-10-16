begin;

insert into auth.users (
  instance_id, id, aud, role, email, encrypted_password, email_confirmed_at,
  raw_app_meta_data, raw_user_meta_data, created_at, updated_at)
values
  ('00000000-0000-0000-0000-000000000000', '10000000-0000-0000-0000-000000000001',
   'authenticated', 'authenticated', 'license-test-1@example.invalid', '', now(), '{}', '{}', now(), now()),
  ('00000000-0000-0000-0000-000000000000', '10000000-0000-0000-0000-000000000002',
   'authenticated', 'authenticated', 'license-test-2@example.invalid', '', now(), '{}', '{}', now(), now()),
  ('00000000-0000-0000-0000-000000000000', '10000000-0000-0000-0000-000000000003',
   'authenticated', 'authenticated', 'license-test-3@example.invalid', '', now(), '{}', '{}', now(), now()),
  ('00000000-0000-0000-0000-000000000000', '10000000-0000-0000-0000-000000000004',
   'authenticated', 'authenticated', 'license-test-4@example.invalid', '', now(), '{}', '{}', now(), now());

update public.license_configuration set launched_at = now(), updated_at = now() where id = true;
select set_config('request.jwt.claim.role', 'authenticated', true);
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000001', true);

select public.license_register_copy(
  '20000000-0000-0000-0000-000000000001', repeat('a', 64), 'Test copy one');
select public.license_register_copy(
  '20000000-0000-0000-0000-000000000002', repeat('a', 64), 'Test copy two');

select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000002', true);
do $test$
begin
  begin
    perform public.license_register_copy(
      '20000000-0000-0000-0000-000000000001', repeat('a', 64), 'Hijacked copy');
    raise exception 'Expected cross-account copy registration to fail';
  exception when others then
    if sqlerrm <> 'Copy identity conflict' then raise; end if;
  end;
end;
$test$;
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000001', true);

do $test$
begin
  if not exists (
    select 1 from public.license_copies
    where copy_id = '20000000-0000-0000-0000-000000000001'
      and user_id = '10000000-0000-0000-0000-000000000001'
      and label = 'Test copy one'
  ) then
    raise exception 'Rejected copy takeover changed the original copy';
  end if;
end;
$test$;

do $test$
begin
  begin
    perform public.license_register_copy(
      '20000000-0000-0000-0000-000000000003', repeat('a', 64), 'Forbidden third copy');
    raise exception 'Expected third copy registration to fail';
  exception when others then
    if sqlerrm = 'Expected third copy registration to fail' then raise; end if;
  end;
end;
$test$;

do $test$
declare v_status jsonb;
begin
  v_status := public.license_status();
  if v_status->>'tier' <> 'pending' or (v_status->>'active_copies')::integer <> 2 then
    raise exception 'Unexpected pending status: %', v_status;
  end if;
end;
$test$;

select public.license_start_or_renew(
  '20000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001');
select public.license_start_or_renew(
  '20000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000002');

do $test$
declare v_status jsonb;
begin
  v_status := public.license_status();
  if v_status->>'tier' <> 'full'
    or (v_status->>'full_access_until')::timestamptz < now() + interval '2 days 23 hours' then
    raise exception 'Three-day trial did not start correctly: %', v_status;
  end if;
  begin
    perform public.license_start_or_renew(
      '20000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000003');
    raise exception 'Expected third concurrent run to fail';
  exception when others then
    if sqlerrm = 'Expected third concurrent run to fail' then raise; end if;
  end;
end;
$test$;

select public.license_stop('30000000-0000-0000-0000-000000000001');
select public.license_stop('30000000-0000-0000-0000-000000000002');
update public.license_accounts
  set trial_started_at = now() - interval '4 days'
  where user_id = '10000000-0000-0000-0000-000000000001';

select public.license_start_or_renew(
  '20000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000004');

do $test$
declare v_status jsonb;
begin
  v_status := public.license_status();
  if v_status->>'tier' <> 'free' or (v_status->>'seconds_remaining')::integer < 10795 then
    raise exception 'Free quota did not start correctly: %', v_status;
  end if;
end;
$test$;

update public.license_leases
  set started_at = now() - interval '10 minutes', ends_at = now() - interval '1 second'
  where run_id = '30000000-0000-0000-0000-000000000004';
select public.license_start_or_renew(
  '20000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000004');
select public.license_stop('30000000-0000-0000-0000-000000000004');

do $test$
declare v_reserved integer;
begin
  select seconds_reserved into v_reserved from public.license_daily_usage
    where user_id = '10000000-0000-0000-0000-000000000001'
      and day_local = (now() at time zone 'Europe/Stockholm')::date;
  if v_reserved <> 600 then
    raise exception 'Expected exactly 600 consumed seconds, got %', v_reserved;
  end if;
end;
$test$;

-- A second account on the same Windows machine goes directly to the free tier.
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000003', true);
select public.license_register_copy(
  '20000000-0000-0000-0000-000000000003', repeat('a', 64), 'Reused machine');
select public.license_start_or_renew(
  '20000000-0000-0000-0000-000000000003', '30000000-0000-0000-0000-000000000006');

do $test$
declare v_status jsonb;
begin
  v_status := public.license_status();
  if v_status->>'tier' <> 'free'
    or (select trial_started_at from public.license_accounts
        where user_id = '10000000-0000-0000-0000-000000000003') is not null then
    raise exception 'Reused machine incorrectly received another trial: %', v_status;
  end if;
end;
$test$;
select public.license_stop('30000000-0000-0000-0000-000000000006');

insert into public.license_promo_codes (code_hash)
values (encode(extensions.digest('TBU-TEST-PROMO-CODE', 'sha256'), 'hex'));
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000002', true);
select public.license_register_copy(
  '20000000-0000-0000-0000-000000000004', repeat('b', 64), 'Promotion copy');
select public.license_redeem_promo('TBU-TEST-PROMO-CODE');
select public.license_start_or_renew(
  '20000000-0000-0000-0000-000000000004', '30000000-0000-0000-0000-000000000005');

do $test$
declare v_status jsonb;
begin
  v_status := public.license_status();
  if v_status->>'tier' <> 'full'
    or (v_status->>'full_access_until')::timestamptz < now() + interval '2 months 29 days' then
    raise exception 'Three-month promotion did not start correctly: %', v_status;
  end if;
end;
$test$;

-- Redeeming in time is insufficient when the first automation start happens after day 30.
insert into public.license_promo_codes (code_hash)
values (encode(extensions.digest('TBU-TEST-EXPIRED-CODE', 'sha256'), 'hex'));
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000004', true);
select public.license_register_copy(
  '20000000-0000-0000-0000-000000000005', repeat('c', 64), 'Expired promotion copy');
select public.license_redeem_promo('TBU-TEST-EXPIRED-CODE');
update public.license_configuration set launched_at = now() - interval '31 days' where id = true;
select public.license_start_or_renew(
  '20000000-0000-0000-0000-000000000005', '30000000-0000-0000-0000-000000000007');

do $test$
declare v_status jsonb;
begin
  v_status := public.license_status();
  if v_status->>'tier' <> 'free'
    or (select promo_started_at from public.license_accounts
        where user_id = '10000000-0000-0000-0000-000000000004') is not null then
    raise exception 'Expired promotion activation incorrectly received full access: %', v_status;
  end if;
end;
$test$;
select public.license_stop('30000000-0000-0000-0000-000000000007');

select set_config('request.jwt.claim.role', 'service_role', true);
do $test$
declare v_recorded boolean;
begin
  v_recorded := public.license_record_payment(
  'evt_license_test_paid', 'checkout.session.completed', 'cs_license_test', 'pi_license_test',
  '10000000-0000-0000-0000-000000000002', 1, now());
  if not v_recorded then raise exception 'First payment event was not recorded'; end if;
  v_recorded := public.license_record_payment(
    'evt_license_test_paid', 'checkout.session.completed', 'cs_license_test', 'pi_license_test',
    '10000000-0000-0000-0000-000000000002', 1, now());
  if v_recorded then raise exception 'Duplicate payment event was recorded twice'; end if;
  if (select count(*) from public.license_purchases where checkout_session_id = 'cs_license_test') <> 1 then
    raise exception 'Duplicate payment created more than one purchase';
  end if;
end;
$test$;
select public.license_record_reversal(
  'evt_license_test_refund', 'charge.refunded', 'pi_license_test');

do $test$
begin
  if not exists (
    select 1 from public.license_purchases
    where checkout_session_id = 'cs_license_test' and refunded_at is not null
  ) then
    raise exception 'Purchase reversal was not recorded';
  end if;
end;
$test$;

insert into public.license_lifetime_grants (user_id, reason, admin_email)
values ('10000000-0000-0000-0000-000000000004', 'Remote integration test', 'test@example.invalid');
select set_config('request.jwt.claim.role', 'authenticated', true);
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000004', true);

do $test$
declare v_status jsonb;
begin
  v_status := public.license_status();
  if v_status->>'tier' <> 'full'
    or (v_status->>'lifetime_access')::boolean is not true
    or (v_status->>'offline_eligible')::boolean is not true
    or extract(year from (v_status->>'full_access_until')::timestamptz) <> 9999 then
    raise exception 'Lifetime access did not resolve correctly: %', v_status;
  end if;
end;
$test$;

rollback;
