-- Tbot account entitlements and usage. Apply through Supabase migrations, not the SQL editor.
create extension if not exists pgcrypto with schema extensions;

create table public.license_configuration (
  id boolean primary key default true check (id),
  launched_at timestamptz,
  updated_at timestamptz not null default now()
);
insert into public.license_configuration (id) values (true);

create table public.license_accounts (
  user_id uuid primary key references auth.users(id) on delete cascade,
  first_automation_at timestamptz,
  trial_started_at timestamptz,
  promo_started_at timestamptz,
  promo_redeemed_at timestamptz,
  promo_code_hash text,
  trial_exception boolean not null default false,
  created_at timestamptz not null default now()
);

create table public.license_copies (
  copy_id uuid primary key,
  user_id uuid not null references auth.users(id) on delete cascade,
  fingerprint_hash text not null check (fingerprint_hash ~ '^[a-f0-9]{64}$'),
  label text not null check (char_length(label) between 1 and 80),
  registered_at timestamptz not null default now(),
  revoked_at timestamptz
);
create index license_copies_user_active on public.license_copies (user_id) where revoked_at is null;

create table public.license_trial_machines (
  fingerprint_hash text primary key check (fingerprint_hash ~ '^[a-f0-9]{64}$'),
  -- Deliberately retained after account deletion so deleting an account cannot reset a machine trial.
  first_user_id uuid not null,
  claimed_at timestamptz not null default now()
);

create table public.license_promo_codes (
  code_hash text primary key check (code_hash ~ '^[a-f0-9]{64}$'),
  claimed_by uuid unique references auth.users(id),
  claimed_at timestamptz,
  created_at timestamptz not null default now(),
  created_by text not null default 'admin'
);

create table public.license_purchases (
  checkout_session_id text primary key,
  payment_intent_id text,
  user_id uuid not null references auth.users(id) on delete cascade,
  months integer not null check (months in (1, 2, 6, 12)),
  amount_eur_cents integer not null check (amount_eur_cents in (299, 499, 1399, 2499)),
  paid_at timestamptz not null,
  refunded_at timestamptz,
  created_at timestamptz not null default now()
);
create index license_purchases_user_paid on public.license_purchases (user_id, paid_at, checkout_session_id)
  where refunded_at is null;
create unique index license_purchases_intent on public.license_purchases (payment_intent_id)
  where payment_intent_id is not null;

create table public.license_admin_grants (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references auth.users(id) on delete cascade,
  months integer not null check (months > 0 and months <= 120),
  granted_at timestamptz not null default now(),
  revoked_at timestamptz,
  reason text not null check (char_length(reason) between 1 and 500),
  admin_email text not null
);

create table public.license_daily_usage (
  user_id uuid not null references auth.users(id) on delete cascade,
  day_local date not null,
  seconds_reserved integer not null default 0 check (seconds_reserved between 0 and 10800),
  primary key (user_id, day_local)
);

create table public.license_leases (
  run_id uuid primary key,
  copy_id uuid not null references public.license_copies(copy_id) on delete cascade,
  user_id uuid not null references auth.users(id) on delete cascade,
  started_at timestamptz not null,
  ends_at timestamptz not null,
  day_local date not null,
  charged_seconds integer not null default 0 check (charged_seconds between 0 and 600),
  check (ends_at > started_at)
);
create index license_leases_copy on public.license_leases (copy_id);

create table public.license_webhook_events (
  stripe_event_id text primary key,
  event_type text not null,
  processed_at timestamptz not null default now()
);

alter table public.license_configuration enable row level security;
alter table public.license_accounts enable row level security;
alter table public.license_copies enable row level security;
alter table public.license_trial_machines enable row level security;
alter table public.license_promo_codes enable row level security;
alter table public.license_purchases enable row level security;
alter table public.license_admin_grants enable row level security;
alter table public.license_daily_usage enable row level security;
alter table public.license_leases enable row level security;
alter table public.license_webhook_events enable row level security;

-- The client reads its own purchases and copies. Mutations use narrow RPCs below.
create policy license_copies_read_own on public.license_copies for select to authenticated
  using (user_id = (select auth.uid()));
create policy license_purchases_read_own on public.license_purchases for select to authenticated
  using (user_id = (select auth.uid()));

revoke all on public.license_configuration, public.license_accounts, public.license_copies,
  public.license_trial_machines, public.license_promo_codes, public.license_purchases,
  public.license_admin_grants, public.license_daily_usage, public.license_leases,
  public.license_webhook_events from anon, authenticated;
grant select on public.license_copies, public.license_purchases to authenticated;

-- Calendar-month purchases are rebuilt from the surviving ledger after a refund.
create function public.license_full_access_until(p_user_id uuid)
returns timestamptz
language plpgsql stable security definer set search_path = public, pg_temp
as $$
declare
  v_account public.license_accounts%rowtype;
  v_until timestamptz;
  v_grant record;
begin
  select * into v_account from public.license_accounts where user_id = p_user_id;
  if v_account.promo_started_at is not null then
    v_until := v_account.promo_started_at + interval '3 months';
  elsif v_account.trial_started_at is not null then
    v_until := v_account.trial_started_at + interval '3 days';
  end if;

  for v_grant in
    select paid_at as granted_at, months, checkout_session_id as grant_id
      from public.license_purchases
      where user_id = p_user_id and refunded_at is null
    union all
    select granted_at, months, id::text
      from public.license_admin_grants
      where user_id = p_user_id and revoked_at is null
    order by granted_at, grant_id
  loop
    -- Purchases made before the first automation start wait behind the trial/promo.
    if v_account.first_automation_at is null then
      continue;
    end if;
    v_until := greatest(coalesce(v_until, v_account.first_automation_at),
      v_grant.granted_at, v_account.first_automation_at)
      + make_interval(months => v_grant.months);
  end loop;

  return v_until;
end;
$$;
revoke all on function public.license_full_access_until(uuid) from public, anon, authenticated;

create function public.license_register_copy(p_copy_id uuid, p_fingerprint_hash text, p_label text)
returns void
language plpgsql security definer set search_path = public, pg_temp
as $$
declare
  v_user_id uuid := auth.uid();
  v_existing public.license_copies%rowtype;
begin
  if v_user_id is null then raise exception 'Authentication required'; end if;
  if p_fingerprint_hash !~ '^[a-f0-9]{64}$' or char_length(trim(p_label)) not between 1 and 80 then
    raise exception 'Invalid copy details';
  end if;

  insert into public.license_accounts (user_id) values (v_user_id) on conflict do nothing;
  perform 1 from public.license_accounts where user_id = v_user_id for update;
  select * into v_existing from public.license_copies where copy_id = p_copy_id;
  if found and (v_existing.user_id <> v_user_id or v_existing.fingerprint_hash <> p_fingerprint_hash) then
    raise exception 'Copy identity conflict';
  end if;

  if not found or v_existing.revoked_at is not null then
    if (select count(*) from public.license_copies where user_id = v_user_id and revoked_at is null) >= 2 then
      raise exception 'Two registered copies are already active';
    end if;
  end if;

  insert into public.license_copies (copy_id, user_id, fingerprint_hash, label)
    values (p_copy_id, v_user_id, p_fingerprint_hash, trim(p_label))
    on conflict (copy_id) do update set label = excluded.label, revoked_at = null;
end;
$$;
revoke all on function public.license_register_copy(uuid, text, text) from public, anon;
grant execute on function public.license_register_copy(uuid, text, text) to authenticated;

create function public.license_remove_copy(p_copy_id uuid)
returns void
language plpgsql security definer set search_path = public, pg_temp
as $$
declare
  v_user_id uuid := auth.uid();
  v_lease public.license_leases%rowtype;
begin
  if v_user_id is null then raise exception 'Authentication required'; end if;
  perform 1 from public.license_accounts where user_id = v_user_id for update;
  update public.license_copies set revoked_at = now()
    where copy_id = p_copy_id and user_id = v_user_id and revoked_at is null;
  if not found then raise exception 'Active copy not found'; end if;
  for v_lease in select * from public.license_leases
    where copy_id = p_copy_id and user_id = v_user_id loop
    if v_lease.charged_seconds > 0 and v_lease.ends_at > now() then
      update public.license_daily_usage
        set seconds_reserved = greatest(0, seconds_reserved - least(v_lease.charged_seconds,
          ceil(extract(epoch from v_lease.ends_at - now()))::integer))
        where user_id = v_user_id and day_local = v_lease.day_local;
    end if;
  end loop;
  delete from public.license_leases where copy_id = p_copy_id and user_id = v_user_id;
end;
$$;
revoke all on function public.license_remove_copy(uuid) from public, anon;
grant execute on function public.license_remove_copy(uuid) to authenticated;

create function public.license_redeem_promo(p_code text)
returns void
language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
  v_user_id uuid := auth.uid();
  v_hash text := encode(digest(upper(trim(p_code)), 'sha256'), 'hex');
  v_launch timestamptz;
begin
  if v_user_id is null then raise exception 'Authentication required'; end if;
  select launched_at into v_launch from public.license_configuration where id = true;
  if v_launch is null or now() > v_launch + interval '30 days' then
    raise exception 'Promotion redemption has closed';
  end if;
  insert into public.license_accounts (user_id) values (v_user_id) on conflict do nothing;
  perform 1 from public.license_accounts where user_id = v_user_id for update;
  if exists (select 1 from public.license_accounts where user_id = v_user_id and promo_code_hash is not null) then
    raise exception 'A promotion is already redeemed on this account';
  end if;
  update public.license_promo_codes set claimed_by = v_user_id, claimed_at = now()
    where code_hash = v_hash and claimed_by is null;
  if not found then raise exception 'Invalid or already redeemed promotion code'; end if;
  update public.license_accounts set promo_code_hash = v_hash, promo_redeemed_at = now()
    where user_id = v_user_id;
end;
$$;
revoke all on function public.license_redeem_promo(text) from public, anon;
grant execute on function public.license_redeem_promo(text) to authenticated;

create function public.license_status()
returns jsonb
language plpgsql security definer set search_path = public, pg_temp
as $$
declare
  v_user_id uuid := auth.uid();
  v_account public.license_accounts%rowtype;
  v_until timestamptz;
  v_day date := (now() at time zone 'Europe/Stockholm')::date;
  v_used integer := 0;
  v_unused_reserved integer := 0;
  v_active_free_runs integer := 0;
  v_launch timestamptz;
begin
  if v_user_id is null then raise exception 'Authentication required'; end if;
  insert into public.license_accounts (user_id) values (v_user_id) on conflict do nothing;
  select * into v_account from public.license_accounts where user_id = v_user_id;
  select launched_at into v_launch from public.license_configuration where id = true;
  v_until := public.license_full_access_until(v_user_id);
  select coalesce(seconds_reserved, 0) into v_used from public.license_daily_usage
    where user_id = v_user_id and day_local = v_day;
  select coalesce(sum(least(charged_seconds,
      greatest(0, ceil(extract(epoch from ends_at - now()))::integer))), 0)::integer,
      count(*)::integer
    into v_unused_reserved, v_active_free_runs
    from public.license_leases
    where user_id = v_user_id and day_local = v_day
      and charged_seconds > 0 and ends_at > now();
  return jsonb_build_object(
    'server_now', now(),
    'tier', case when v_account.first_automation_at is null then 'pending'
      when v_until > now() then 'full' else 'free' end,
    'full_access_until', v_until,
    'offline_eligible', v_until > now() and (
      exists (select 1 from public.license_purchases
        where user_id = v_user_id and refunded_at is null)
      or exists (select 1 from public.license_admin_grants
        where user_id = v_user_id and revoked_at is null)),
    'promo_redeemed', v_account.promo_code_hash is not null,
    'promo_activation_available', v_account.promo_code_hash is not null
      and v_account.promo_started_at is null
      and v_launch is not null and now() <= v_launch + interval '30 days',
    'pending_paid_months', case when v_account.first_automation_at is null then
      (select coalesce(sum(months), 0) from public.license_purchases
        where user_id = v_user_id and refunded_at is null) else 0 end,
    -- Reserved lease time has not been consumed yet. Add its unused portion back so the
    -- countdown reflects active seconds rather than ten-minute reservation blocks.
    'seconds_remaining', greatest(0, 10800 - coalesce(v_used, 0) + v_unused_reserved),
    'active_free_runs', v_active_free_runs,
    'next_reset_at', (v_day + 1)::timestamp at time zone 'Europe/Stockholm',
    'active_copies', (select count(*) from public.license_copies
      where user_id = v_user_id and revoked_at is null)
  );
end;
$$;
revoke all on function public.license_status() from public, anon;
grant execute on function public.license_status() to authenticated;

create function public.license_start_or_renew(p_copy_id uuid, p_run_id uuid)
returns jsonb
language plpgsql security definer set search_path = public, pg_temp
as $$
declare
  v_user_id uuid := auth.uid();
  v_account public.license_accounts%rowtype;
  v_copy public.license_copies%rowtype;
  v_lease public.license_leases%rowtype;
  v_launch timestamptz;
  v_until timestamptz;
  v_lease_until timestamptz;
  v_day date := (now() at time zone 'Europe/Stockholm')::date;
  v_reset timestamptz := (v_day + 1)::timestamp at time zone 'Europe/Stockholm';
  v_seconds integer;
  v_used integer;
  v_tier text;
begin
  if v_user_id is null then raise exception 'Authentication required'; end if;
  insert into public.license_accounts (user_id) values (v_user_id) on conflict do nothing;
  select * into v_account from public.license_accounts where user_id = v_user_id for update;
  select * into v_copy from public.license_copies
    where copy_id = p_copy_id and user_id = v_user_id and revoked_at is null;
  if not found then raise exception 'Register this copy before starting automation'; end if;

  select * into v_lease from public.license_leases where run_id = p_run_id;
  if found and (v_lease.user_id <> v_user_id or v_lease.copy_id <> p_copy_id) then
    raise exception 'Run identity conflict';
  end if;
  if found and v_lease.ends_at > now() + interval '30 seconds' then
    return jsonb_build_object('tier', case when v_lease.charged_seconds > 0 then 'free' else 'full' end,
      'lease_until', v_lease.ends_at, 'server_now', now());
  end if;
  if found and v_lease.ends_at > now() and v_lease.charged_seconds > 0 then
    -- A renewal replaces the nearly expired reservation. Return its unused seconds before
    -- reserving the next block so quota is charged once and the client avoids a request loop.
    update public.license_daily_usage
      set seconds_reserved = greatest(0, seconds_reserved - least(v_lease.charged_seconds,
        ceil(extract(epoch from v_lease.ends_at - now()))::integer))
      where user_id = v_user_id and day_local = v_lease.day_local;
  end if;
  delete from public.license_leases where run_id = p_run_id;
  if (select count(*) from public.license_leases
      where user_id = v_user_id and ends_at > now()) >= 2 then
    raise exception 'Two program copies are already running';
  end if;

  select launched_at into v_launch from public.license_configuration where id = true;
  if v_account.first_automation_at is null then
    update public.license_accounts set first_automation_at = now() where user_id = v_user_id;
  end if;
  if v_account.promo_code_hash is not null and v_account.promo_started_at is null
    and v_launch is not null and now() <= v_launch + interval '30 days' then
    update public.license_accounts set promo_started_at = now() where user_id = v_user_id;
  elsif v_account.promo_code_hash is null and v_account.trial_started_at is null
    and v_account.first_automation_at is null then
    insert into public.license_trial_machines (fingerprint_hash, first_user_id)
      values (v_copy.fingerprint_hash, v_user_id) on conflict do nothing;
    if (select first_user_id from public.license_trial_machines
      where fingerprint_hash = v_copy.fingerprint_hash) = v_user_id
      or v_account.trial_exception then
      update public.license_accounts set trial_started_at = now() where user_id = v_user_id;
    end if;
  end if;

  v_until := public.license_full_access_until(v_user_id);
  if v_until > now() then
    v_tier := 'full';
    v_lease_until := least(now() + interval '10 minutes', v_until);
    v_seconds := 0;
  else
    v_tier := 'free';
    insert into public.license_daily_usage (user_id, day_local)
      values (v_user_id, v_day) on conflict do nothing;
    select seconds_reserved into v_used from public.license_daily_usage
      where user_id = v_user_id and day_local = v_day for update;
    v_seconds := least(600, 10800 - v_used,
      greatest(0, floor(extract(epoch from v_reset - now()))::integer));
    if v_seconds <= 0 then raise exception 'Daily free limit reached'; end if;
    v_lease_until := now() + make_interval(secs => v_seconds);
    update public.license_daily_usage set seconds_reserved = seconds_reserved + v_seconds
      where user_id = v_user_id and day_local = v_day;
  end if;

  insert into public.license_leases
    (run_id, copy_id, user_id, started_at, ends_at, day_local, charged_seconds)
    values (p_run_id, p_copy_id, v_user_id, now(), v_lease_until,
      v_day, case when v_tier = 'free' then v_seconds else 0 end);
  return jsonb_build_object('tier', v_tier, 'lease_until', v_lease_until,
    'server_now', now());
end;
$$;
revoke all on function public.license_start_or_renew(uuid, uuid) from public, anon;
grant execute on function public.license_start_or_renew(uuid, uuid) to authenticated;

create function public.license_stop(p_run_id uuid)
returns void
language plpgsql security definer set search_path = public, pg_temp
as $$
declare
  v_user_id uuid := auth.uid();
  v_lease public.license_leases%rowtype;
begin
  if v_user_id is null then raise exception 'Authentication required'; end if;
  perform 1 from public.license_accounts where user_id = v_user_id for update;
  select * into v_lease from public.license_leases
    where run_id = p_run_id and user_id = v_user_id for update;
  if not found then return; end if;
  if v_lease.charged_seconds > 0 and v_lease.ends_at > now() then
    update public.license_daily_usage
      set seconds_reserved = greatest(0, seconds_reserved - least(v_lease.charged_seconds,
        ceil(extract(epoch from v_lease.ends_at - now()))::integer))
      where user_id = v_user_id and day_local = v_lease.day_local;
  end if;
  delete from public.license_leases where run_id = p_run_id and user_id = v_user_id;
end;
$$;
revoke all on function public.license_stop(uuid) from public, anon;
grant execute on function public.license_stop(uuid) to authenticated;

-- Only the Stripe webhook, authenticated with the service-role secret, may fulfill purchases.
create function public.license_record_payment(
  p_event_id text, p_event_type text, p_session_id text, p_payment_intent_id text,
  p_user_id uuid, p_months integer, p_paid_at timestamptz)
returns boolean
language plpgsql security definer set search_path = public, pg_temp
as $$
declare v_price integer;
begin
  if auth.role() <> 'service_role' then raise exception 'Service role required'; end if;
  if p_months not in (1, 2, 6, 12) or p_event_id is null or p_session_id is null
    or p_event_type not in ('checkout.session.completed', 'checkout.session.async_payment_succeeded') then
    raise exception 'Invalid purchase';
  end if;
  v_price := case p_months when 1 then 299 when 2 then 499
    when 6 then 1399 when 12 then 2499 end;
  insert into public.license_webhook_events (stripe_event_id, event_type)
    values (p_event_id, p_event_type) on conflict do nothing;
  if not found then return false; end if;
  insert into public.license_accounts (user_id) values (p_user_id) on conflict do nothing;
  perform 1 from public.license_accounts where user_id = p_user_id for update;
  insert into public.license_purchases
    (checkout_session_id, payment_intent_id, user_id, months, amount_eur_cents, paid_at)
    values (p_session_id, p_payment_intent_id, p_user_id, p_months, v_price, p_paid_at)
    on conflict (checkout_session_id) do nothing;
  if not found and not exists (
    select 1 from public.license_purchases where checkout_session_id = p_session_id
      and user_id = p_user_id and months = p_months
  ) then raise exception 'Checkout session identity conflict'; end if;
  return true;
end;
$$;
revoke all on function public.license_record_payment(text, text, text, text, uuid, integer, timestamptz)
  from public, anon, authenticated;
grant execute on function public.license_record_payment(text, text, text, text, uuid, integer, timestamptz)
  to service_role;

create function public.license_record_reversal(
  p_event_id text, p_event_type text, p_payment_intent_id text)
returns boolean
language plpgsql security definer set search_path = public, pg_temp
as $$
declare v_user_id uuid;
begin
  if auth.role() <> 'service_role' then raise exception 'Service role required'; end if;
  if p_payment_intent_id is null or p_event_type not in ('charge.refunded', 'charge.dispute.created') then
    raise exception 'Invalid reversal';
  end if;
  insert into public.license_webhook_events (stripe_event_id, event_type)
    values (p_event_id, p_event_type) on conflict do nothing;
  if not found then return false; end if;
  select user_id into v_user_id from public.license_purchases
    where payment_intent_id = p_payment_intent_id;
  if not found then raise exception 'Purchase for reversal has not arrived yet'; end if;
  perform 1 from public.license_accounts where user_id = v_user_id for update;
  update public.license_purchases set refunded_at = now()
    where payment_intent_id = p_payment_intent_id and refunded_at is null
    returning user_id into v_user_id;
  if found then
    -- Recheck full access immediately instead of letting a paid lease run to its old expiry.
    delete from public.license_leases where user_id = v_user_id and charged_seconds = 0;
  end if;
  return true;
end;
$$;
revoke all on function public.license_record_reversal(text, text, text) from public, anon, authenticated;
grant execute on function public.license_record_reversal(text, text, text) to service_role;
