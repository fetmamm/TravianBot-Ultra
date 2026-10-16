// Stripe sends raw JSON. Verify the signature before accepting or parsing an event.
const supabaseUrl = Deno.env.get("SUPABASE_URL") ?? "";
const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") ?? "";
const webhookSecret = Deno.env.get("STRIPE_WEBHOOK_SECRET") ?? "";

function constantTimeEqual(left: Uint8Array, right: Uint8Array): boolean {
  if (left.length !== right.length) return false;
  let difference = 0;
  for (let i = 0; i < left.length; i++) difference |= left[i] ^ right[i];
  return difference === 0;
}

async function verifySignature(payload: string, header: string): Promise<boolean> {
  if (!webhookSecret || !header) return false;
  const parts = header.split(",").map((part) => part.trim().split("="));
  const timestamp = parts.find(([key]) => key === "t")?.[1];
  const signatures = parts.filter(([key]) => key === "v1").map(([, value]) => value);
  if (!timestamp || signatures.length === 0 || !/^\d+$/.test(timestamp)) return false;
  if (Math.abs(Date.now() / 1000 - Number(timestamp)) > 300) return false;

  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(webhookSecret),
    { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const expected = new Uint8Array(await crypto.subtle.sign("HMAC", key,
    new TextEncoder().encode(`${timestamp}.${payload}`)));
  return signatures.some((signature) => {
    if (!/^[a-f0-9]{64}$/.test(signature)) return false;
    const actual = new Uint8Array(signature.match(/../g)!.map((byte) => parseInt(byte, 16)));
    return constantTimeEqual(expected, actual);
  });
}

async function rpc(name: string, body: unknown): Promise<void> {
  const result = await fetch(`${supabaseUrl}/rest/v1/rpc/${name}`, {
    method: "POST",
    headers: {
      apikey: serviceKey,
      Authorization: `Bearer ${serviceKey}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify(body),
  });
  if (!result.ok) {
    const detail = await result.text();
    throw new Error(`Webhook RPC ${name} failed: ${result.status} ${detail}`);
  }
}

Deno.serve(async (request) => {
  if (request.method !== "POST") return new Response("Method not allowed", { status: 405 });
  if (!supabaseUrl || !serviceKey || !webhookSecret) {
    console.error("[license] Stripe webhook is not configured");
    return new Response("Not configured", { status: 503 });
  }

  const payload = await request.text();
  if (!await verifySignature(payload, request.headers.get("stripe-signature") ?? "")) {
    console.error("[license] Stripe signature verification failed");
    return new Response("Invalid signature", { status: 400 });
  }

  try {
    const event = JSON.parse(payload);
    const item = event.data?.object;
    if (typeof event.id !== "string" || typeof event.type !== "string" || !item) {
      return new Response("Invalid event", { status: 400 });
    }

    if (event.type === "checkout.session.completed" ||
        event.type === "checkout.session.async_payment_succeeded") {
      if (item.payment_status !== "paid") return new Response("ok");
      const userId = item.metadata?.license_user_id;
      const months = Number(item.metadata?.license_months);
      const expectedCents: Record<number, number> = { 1: 299, 2: 499, 6: 1399, 12: 2499 };
      if (item.mode !== "payment" || userId !== item.client_reference_id ||
          ![1, 2, 6, 12].includes(months) || typeof item.id !== "string" ||
          item.currency?.toLowerCase() !== "eur" || item.amount_total !== expectedCents[months]) {
        throw new Error("Checkout metadata mismatch");
      }
      const intentId = typeof item.payment_intent === "string" ? item.payment_intent :
        item.payment_intent?.id;
      if (typeof intentId !== "string") throw new Error("Checkout payment intent missing");
      await rpc("license_record_payment", {
        p_event_id: event.id,
        p_event_type: event.type,
        p_session_id: item.id,
        p_payment_intent_id: intentId,
        p_user_id: userId,
        p_months: months,
        p_paid_at: new Date(event.created * 1000).toISOString(),
      });
      console.info("[license] Purchase recorded", item.id, userId, months);
    } else if (event.type === "charge.refunded" || event.type === "refund.created" ||
        event.type === "charge.dispute.created") {
      const intentId = typeof item.payment_intent === "string" ? item.payment_intent :
        item.payment_intent?.id;
      if (typeof intentId !== "string") throw new Error("Reversal payment intent missing");
      await rpc("license_record_reversal", {
        p_event_id: event.id,
        p_event_type: event.type,
        p_payment_intent_id: intentId,
      });
      console.info("[license] Purchase reversed", intentId, event.type);
    }
    return new Response("ok");
  } catch (error) {
    console.error("[license] Stripe event processing failed", error);
    return new Response("Retry event", { status: 500 });
  }
});
