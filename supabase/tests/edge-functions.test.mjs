import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import test from "node:test";

function installDeno(env) {
  let handler;
  globalThis.Deno = {
    env: { get: (name) => env[name] },
    serve: (candidate) => { handler = candidate; },
  };
  return () => handler;
}

test("license API authenticates status and creates managed Checkout", async () => {
  const env = {
    SUPABASE_URL: "https://project.supabase.co",
    SUPABASE_ANON_KEY: "public-key",
    SITE_ORIGIN: "https://tbot.example",
    CHECKOUT_ENABLED: "true",
    STRIPE_SECRET_KEY: "stripe-secret",
    STRIPE_PRICE_1_MONTH: "price_one",
    CHECKOUT_SUCCESS_URL: "https://tbot.example/account.html?checkout=success",
    CHECKOUT_CANCEL_URL: "https://tbot.example/account.html?checkout=cancel",
  };
  const getHandler = installDeno(env);
  const requests = [];
  let rpcResponseIsEmpty = false;
  globalThis.fetch = async (url, options = {}) => {
    requests.push({ url: String(url), options });
    if (String(url).endsWith("/auth/v1/user")) {
      return Response.json({ id: "00000000-0000-0000-0000-000000000001",
        email: "person@example.com", email_confirmed_at: "2026-10-15T10:00:00Z" });
    }
    if (String(url).includes("/rest/v1/rpc/license_status")) {
      if (rpcResponseIsEmpty) return new Response(null, { status: 200 });
      return Response.json({ tier: "pending", seconds_remaining: 10800 });
    }
    if (String(url).includes("/rest/v1/rpc/license_stop")) {
      return new Response(null, { status: 204 });
    }
    if (String(url) === "https://api.stripe.com/v1/checkout/sessions") {
      return Response.json({ id: "cs_test_1", url: "https://checkout.stripe.com/test" });
    }
    throw new Error(`Unexpected request: ${url}`);
  };
  await import(new URL("../functions/license-api/index.ts?license-test", import.meta.url));
  const handler = getHandler();

  const denied = await handler(new Request("https://edge.example/license-api/status", {
    headers: { Authorization: "Bearer user-token", Origin: "https://evil.example" },
  }));
  assert.equal(denied.status, 403);

  const status = await handler(new Request("https://edge.example/license-api/status", {
    headers: { Authorization: "Bearer user-token", Origin: "https://tbot.example" },
  }));
  assert.equal(status.status, 200);
  assert.equal(status.headers.get("access-control-allow-origin"), "https://tbot.example");
  assert.deepEqual(await status.json(), {
    tier: "pending", seconds_remaining: 10800, checkout_enabled: true,
  });

  const checkout = await handler(new Request("https://edge.example/license-api/checkout", {
    method: "POST",
    headers: { Authorization: "Bearer user-token", "Content-Type": "application/json" },
    body: JSON.stringify({ months: 1 }),
  }));
  assert.equal(checkout.status, 200);
  const stripeRequest = requests.find((item) => item.url.includes("api.stripe.com"));
  assert.ok(stripeRequest);
  assert.equal(stripeRequest.options.body.get("managed_payments[enabled]"), "true");
  assert.equal(stripeRequest.options.body.get("metadata[license_months]"), "1");

  delete env.CHECKOUT_ENABLED;
  const requestCount = requests.length;
  const disabledCheckout = await handler(new Request("https://edge.example/license-api/checkout", {
    method: "POST",
    headers: { Authorization: "Bearer user-token", "Content-Type": "application/json" },
    body: JSON.stringify({ months: 1 }),
  }));
  assert.equal(disabledCheckout.status, 503);
  assert.equal((await disabledCheckout.json()).error, "Checkout is currently unavailable");
  assert.equal(requests.length, requestCount + 1);

  rpcResponseIsEmpty = true;
  const emptyRpc = await handler(new Request("https://edge.example/license-api/status", {
    headers: { Authorization: "Bearer user-token" },
  }));
  assert.equal(emptyRpc.status, 400);
  assert.deepEqual(await emptyRpc.json(), { error: "License service returned an empty response" });

  rpcResponseIsEmpty = false;
  const stopped = await handler(new Request("https://edge.example/license-api/stop", {
    method: "POST",
    headers: { Authorization: "Bearer user-token", "Content-Type": "application/json" },
    body: JSON.stringify({ run_id: "00000000-0000-0000-0000-000000000002" }),
  }));
  assert.equal(stopped.status, 200);
  assert.deepEqual(await stopped.json(), { ok: true });
});

test("Stripe webhook rejects bad signatures and records a paid EUR purchase", async () => {
  const getHandler = installDeno({
    SUPABASE_URL: "https://project.supabase.co",
    SUPABASE_SERVICE_ROLE_KEY: "service-key",
    STRIPE_WEBHOOK_SECRET: "webhook-secret",
  });
  const rpcCalls = [];
  globalThis.fetch = async (url, options = {}) => {
    rpcCalls.push({ url: String(url), body: JSON.parse(options.body) });
    return new Response(null, { status: 204 });
  };
  await import(new URL("../functions/stripe-webhook/index.ts?webhook-test", import.meta.url));
  const handler = getHandler();
  const created = Math.floor(Date.now() / 1000);
  const payload = JSON.stringify({
    id: "evt_1", type: "checkout.session.completed", created,
    data: { object: {
      id: "cs_1", mode: "payment", payment_status: "paid", currency: "eur", amount_total: 299,
      client_reference_id: "00000000-0000-0000-0000-000000000001",
      payment_intent: "pi_1",
      metadata: { license_user_id: "00000000-0000-0000-0000-000000000001", license_months: "1" },
    } },
  });

  const rejected = await handler(new Request("https://edge.example/stripe-webhook", {
    method: "POST", headers: { "stripe-signature": `t=${created},v1=${"0".repeat(64)}` }, body: payload,
  }));
  assert.equal(rejected.status, 400);
  assert.equal(rpcCalls.length, 0);

  const signature = createHmac("sha256", "webhook-secret").update(`${created}.${payload}`).digest("hex");
  const accepted = await handler(new Request("https://edge.example/stripe-webhook", {
    method: "POST", headers: { "stripe-signature": `t=${created},v1=${signature}` }, body: payload,
  }));
  assert.equal(accepted.status, 200);
  assert.equal(rpcCalls.length, 1);
  assert.match(rpcCalls[0].url, /license_record_payment$/);
  assert.equal(rpcCalls[0].body.p_months, 1);

  const refundPayload = JSON.stringify({
    id: "evt_refund_1", type: "refund.created", created,
    data: { object: { id: "re_1", payment_intent: "pi_1" } },
  });
  const refundSignature = createHmac("sha256", "webhook-secret")
    .update(`${created}.${refundPayload}`).digest("hex");
  const refunded = await handler(new Request("https://edge.example/stripe-webhook", {
    method: "POST",
    headers: { "stripe-signature": `t=${created},v1=${refundSignature}` },
    body: refundPayload,
  }));
  assert.equal(refunded.status, 200);
  assert.equal(rpcCalls.length, 2);
  assert.match(rpcCalls[1].url, /license_record_reversal$/);
  assert.equal(rpcCalls[1].body.p_payment_intent_id, "pi_1");
});
