// Supabase Edge Function. All write operations authenticate the Tbot account token.
const supabaseUrl = Deno.env.get("SUPABASE_URL") ?? "";
const anonKey = Deno.env.get("SUPABASE_ANON_KEY") ?? "";
const siteOrigin = Deno.env.get("SITE_ORIGIN") ?? "";

function isCheckoutEnabled(): boolean {
  return Deno.env.get("CHECKOUT_ENABLED") === "true";
}

function response(body: unknown, status = 200, origin?: string): Response {
  const headers: Record<string, string> = { "Content-Type": "application/json" };
  if (origin && origin === siteOrigin) {
    headers["Access-Control-Allow-Origin"] = origin;
    headers["Access-Control-Allow-Headers"] = "authorization, apikey, content-type";
    headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    headers.Vary = "Origin";
  }
  return new Response(JSON.stringify(body), { status, headers });
}

async function rpc(name: string, body: unknown, token: string,
  allowEmptySuccess = false): Promise<unknown> {
  const result = await fetch(`${supabaseUrl}/rest/v1/rpc/${name}`, {
    method: "POST",
    headers: {
      apikey: anonKey,
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify(body),
  });
  const responseText = await result.text();
  if (!responseText.trim()) {
    if (result.ok && allowEmptySuccess) return null;
    if (!result.ok) throw new Error(`RPC ${name} failed (${result.status})`);
    throw new Error("License service returned an empty response");
  }
  let data: any;
  try {
    data = JSON.parse(responseText);
  } catch {
    throw new Error("License service returned an invalid response");
  }
  if (!result.ok) {
    throw new Error(typeof data.message === "string" ? data.message : `RPC ${name} failed`);
  }
  return data;
}

async function getUser(token: string): Promise<{ id: string; email: string | null }> {
  const result = await fetch(`${supabaseUrl}/auth/v1/user`, {
    headers: { apikey: anonKey, Authorization: `Bearer ${token}` },
  });
  if (!result.ok) throw new Error("Invalid account session");
  const data = await result.json();
  if (!data.id) throw new Error("Invalid account session");
  if (!data.email_confirmed_at) throw new Error("Verify your email before using the license");
  return { id: data.id, email: data.email ?? null };
}

async function createCheckout(months: number, user: { id: string; email: string | null }): Promise<string> {
  const priceIds: Record<number, string | undefined> = {
    1: Deno.env.get("STRIPE_PRICE_1_MONTH"),
    2: Deno.env.get("STRIPE_PRICE_2_MONTHS"),
    6: Deno.env.get("STRIPE_PRICE_6_MONTHS"),
    12: Deno.env.get("STRIPE_PRICE_12_MONTHS"),
  };
  const priceId = priceIds[months];
  const stripeKey = Deno.env.get("STRIPE_SECRET_KEY");
  const successUrl = Deno.env.get("CHECKOUT_SUCCESS_URL");
  const cancelUrl = Deno.env.get("CHECKOUT_CANCEL_URL");
  if (!priceId || !stripeKey || !successUrl || !cancelUrl) {
    throw new Error("Checkout is not configured");
  }

  const form = new URLSearchParams({
    mode: "payment",
    "managed_payments[enabled]": "true",
    "line_items[0][price]": priceId,
    "line_items[0][quantity]": "1",
    client_reference_id: user.id,
    "metadata[license_user_id]": user.id,
    "metadata[license_months]": String(months),
    success_url: successUrl,
    cancel_url: cancelUrl,
  });
  if (user.email) form.set("customer_email", user.email);

  const result = await fetch("https://api.stripe.com/v1/checkout/sessions", {
    method: "POST",
    headers: {
      Authorization: `Bearer ${stripeKey}`,
      "Content-Type": "application/x-www-form-urlencoded",
      "Stripe-Version": "2025-03-31.basil",
    },
    body: form,
  });
  const data = await result.json();
  if (!result.ok || typeof data.url !== "string") {
    console.error("[license] Stripe Checkout creation failed", result.status, data.error?.type);
    throw new Error("Could not create checkout session");
  }
  console.info("[license] Checkout session created", data.id, user.id, months);
  return data.url;
}

Deno.serve(async (request) => {
  const origin = request.headers.get("origin") ?? undefined;
  if (origin && origin !== siteOrigin) return response({ error: "Origin not allowed" }, 403);
  if (request.method === "OPTIONS") {
    const headers: Record<string, string> = {};
    if (origin === siteOrigin) {
      headers["Access-Control-Allow-Origin"] = origin;
      headers["Access-Control-Allow-Headers"] = "authorization, apikey, content-type";
      headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    }
    return new Response(null, { status: 204, headers });
  }

  const authorization = request.headers.get("authorization") ?? "";
  const token = authorization.startsWith("Bearer ") ? authorization.slice(7) : "";
  if (!token) return response({ error: "Sign in is required" }, 401, origin);

  try {
    const user = await getUser(token);
    const action = new URL(request.url).pathname.split("/").filter(Boolean).pop();
    if (request.method === "GET" && action === "status") {
      const status = await rpc("license_status", {}, token);
      if (!status || typeof status !== "object" || Array.isArray(status)) {
        throw new Error("License status is invalid");
      }
      return response({ ...status, checkout_enabled: isCheckoutEnabled() }, 200, origin);
    }
    if (request.method !== "POST") return response({ error: "Method not allowed" }, 405, origin);
    const data = await request.json();
    switch (action) {
      case "register-copy":
        await rpc("license_register_copy", {
          p_copy_id: data.copy_id,
          p_fingerprint_hash: data.fingerprint_hash,
          p_label: data.label,
        }, token, true);
        return response({ ok: true }, 200, origin);
      case "remove-copy":
        await rpc("license_remove_copy", { p_copy_id: data.copy_id }, token, true);
        return response({ ok: true }, 200, origin);
      case "redeem":
        await rpc("license_redeem_promo", { p_code: data.code }, token, true);
        return response({ ok: true }, 200, origin);
      case "lease":
        return response(await rpc("license_start_or_renew", {
          p_copy_id: data.copy_id,
          p_run_id: data.run_id,
        }, token), 200, origin);
      case "stop":
        await rpc("license_stop", { p_run_id: data.run_id }, token, true);
        return response({ ok: true }, 200, origin);
      case "checkout":
        if (!isCheckoutEnabled()) {
          return response({ error: "Checkout is currently unavailable" }, 503, origin);
        }
        if (![1, 2, 6, 12].includes(data.months)) return response({ error: "Invalid package" }, 400, origin);
        return response({ url: await createCheckout(data.months, user) }, 200, origin);
      default:
        return response({ error: "Unknown action" }, 404, origin);
    }
  } catch (error) {
    const message = error instanceof Error ? error.message : "Unknown license error";
    console.error("[license] Request failed", message);
    const status = message === "Invalid account session" ? 401 : 400;
    return response({ error: message }, status, origin);
  }
});
