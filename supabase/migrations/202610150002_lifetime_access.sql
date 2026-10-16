-- Auditable permanent access for the owner and exceptional support cases.
create table public.license_lifetime_grants (
  user_id uuid primary key references auth.users(id) on delete cascade,
  granted_at timestamptz not null default now(),
  revoked_at timestamptz,
  reason text not null check (char_length(reason) between 1 and 500),
  admin_email text not null
);

alter table public.license_lifetime_grants enable row level security;
revoke all on public.license_lifetime_grants from anon, authenticated;

create or replace function public.license_full_access_until(p_user_id uuid)
returns timestamptz
language plpgsql stable security definer set search_path = public, pg_temp
as $$
declare
  v_account public.license_accounts%rowtype;
  v_until timestamptz;
  v_grant record;
begin
  if exists (
    select 1 from public.license_lifetime_grants
    where user_id = p_user_id and revoked_at is null
  ) then
    return timestamptz '9999-12-31 23:59:59+00';
  end if;

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

create or replace function public.license_status()
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
  v_lifetime boolean := false;
begin
  if v_user_id is null then raise exception 'Authentication required'; end if;
  insert into public.license_accounts (user_id) values (v_user_id) on conflict do nothing;
  select * into v_account from public.license_accounts where user_id = v_user_id;
  select launched_at into v_launch from public.license_configuration where id = true;
  select exists (
    select 1 from public.license_lifetime_grants
    where user_id = v_user_id and revoked_at is null
  ) into v_lifetime;
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
    'tier', case when v_until > now() then 'full'
      when v_account.first_automation_at is null then 'pending' else 'free' end,
    'full_access_until', v_until,
    'lifetime_access', v_lifetime,
    'offline_eligible', v_until > now() and (
      v_lifetime
      or exists (select 1 from public.license_purchases
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
