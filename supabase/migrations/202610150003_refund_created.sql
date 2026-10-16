-- Stripe recommends refund.created for refund details; keep charge.refunded for compatibility.
create or replace function public.license_record_reversal(
  p_event_id text, p_event_type text, p_payment_intent_id text)
returns boolean
language plpgsql security definer set search_path = public, pg_temp
as $$
declare v_user_id uuid;
begin
  if auth.role() <> 'service_role' then raise exception 'Service role required'; end if;
  if p_payment_intent_id is null
    or p_event_type not in ('charge.refunded', 'refund.created', 'charge.dispute.created') then
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
    delete from public.license_leases where user_id = v_user_id and charged_seconds = 0;
  end if;
  return true;
end;
$$;
revoke all on function public.license_record_reversal(text, text, text) from public, anon, authenticated;
grant execute on function public.license_record_reversal(text, text, text) to service_role;
