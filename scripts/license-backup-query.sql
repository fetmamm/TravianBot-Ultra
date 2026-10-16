select jsonb_build_object(
  'format_version', 1,
  'created_at', now(),
  'project_ref', 'sfgyrrduvbyxywahsjil',
  'tables', jsonb_build_object(
    'users', (select coalesce(jsonb_agg(jsonb_build_object(
      'id', id,
      'email', email,
      'created_at', created_at,
      'email_confirmed_at', email_confirmed_at
    ) order by id), '[]'::jsonb) from auth.users),
    'license_configuration', (select coalesce(jsonb_agg(to_jsonb(t) order by id), '[]'::jsonb)
      from public.license_configuration t),
    'license_accounts', (select coalesce(jsonb_agg(to_jsonb(t) order by user_id), '[]'::jsonb)
      from public.license_accounts t),
    'license_copies', (select coalesce(jsonb_agg(to_jsonb(t) order by copy_id), '[]'::jsonb)
      from public.license_copies t),
    'license_trial_machines', (select coalesce(jsonb_agg(to_jsonb(t) order by fingerprint_hash), '[]'::jsonb)
      from public.license_trial_machines t),
    'license_promo_codes', (select coalesce(jsonb_agg(to_jsonb(t) order by code_hash), '[]'::jsonb)
      from public.license_promo_codes t),
    'license_purchases', (select coalesce(jsonb_agg(to_jsonb(t) order by checkout_session_id), '[]'::jsonb)
      from public.license_purchases t),
    'license_admin_grants', (select coalesce(jsonb_agg(to_jsonb(t) order by id), '[]'::jsonb)
      from public.license_admin_grants t),
    'license_lifetime_grants', (select coalesce(jsonb_agg(to_jsonb(t) order by user_id), '[]'::jsonb)
      from public.license_lifetime_grants t),
    'license_daily_usage', (select coalesce(jsonb_agg(to_jsonb(t) order by user_id, day_local), '[]'::jsonb)
      from public.license_daily_usage t),
    'license_leases', (select coalesce(jsonb_agg(to_jsonb(t) order by run_id), '[]'::jsonb)
      from public.license_leases t),
    'license_webhook_events', (select coalesce(jsonb_agg(to_jsonb(t) order by stripe_event_id), '[]'::jsonb)
      from public.license_webhook_events t)
  )
) as snapshot;
