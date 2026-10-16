(() => {
  "use strict";

  const config = window.TBOT_CONFIG;
  const sessionKey = "tbot.account.session.v1";
  const apiHeaders = { apikey: config.publishableKey, "Content-Type": "application/json" };
  let checkoutAvailable = config.checkoutEnabled && document.body.dataset.page !== "account";

  document.querySelectorAll("[data-year]").forEach((element) => {
    element.textContent = new Date().getFullYear().toString();
  });
  document.querySelectorAll("[data-download]").forEach((link) => {
    link.href = config.downloadUrl;
  });
  document.querySelectorAll("[data-buy]").forEach((button) => {
    if (!config.checkoutEnabled) {
      button.disabled = true;
      button.title = "Payments are not open yet.";
    }
  });
  document.querySelectorAll("[data-checkout-mode]").forEach((element) => {
    if (config.checkoutMode === "sandbox") {
      element.textContent = "Sandbox checkout is enabled for testing. No real charge will be made.";
      element.hidden = false;
    }
  });

  if (document.body.dataset.page !== "account") return;

  const elements = {
    notice: document.querySelector("#notice"), authPanel: document.querySelector("#auth-panel"),
    authForm: document.querySelector("#auth-form"), email: document.querySelector("#email"),
    password: document.querySelector("#password"), passwordRow: document.querySelector("#password-row"),
    authSubmit: document.querySelector("#auth-submit"), authHelp: document.querySelector("#auth-help"),
    dashboard: document.querySelector("#dashboard"), recoveryPanel: document.querySelector("#recovery-panel"),
    recoveryForm: document.querySelector("#recovery-form"), newPassword: document.querySelector("#new-password"),
    accountEmail: document.querySelector("#account-email"), licenseTier: document.querySelector("#license-tier"),
    licenseDetail: document.querySelector("#license-detail"), copies: document.querySelector("#copies"),
    copyCount: document.querySelector("#copy-count"), purchases: document.querySelector("#purchases"),
  };
  let authMode = "signin";
  let session = readSession();

  function readSession() {
    try { return JSON.parse(localStorage.getItem(sessionKey) || "null"); }
    catch { localStorage.removeItem(sessionKey); return null; }
  }

  function saveSession(value) {
    session = value;
    if (value) localStorage.setItem(sessionKey, JSON.stringify(value));
    else localStorage.removeItem(sessionKey);
  }

  function showNotice(message, error = false) {
    elements.notice.textContent = message;
    elements.notice.classList.toggle("error", error);
    elements.notice.hidden = false;
  }

  function clearNotice() { elements.notice.hidden = true; }

  async function parseResponse(response) {
    const text = await response.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = null; }
    if (!response.ok) {
      throw new Error(data?.error_description || data?.msg || data?.error || data?.message ||
        `Request failed (${response.status}).`);
    }
    return data;
  }

  async function authRequest(path, body, method = "POST") {
    return parseResponse(await fetch(`${config.supabaseUrl}/auth/v1/${path}`, {
      method, headers: apiHeaders, body: body === undefined ? undefined : JSON.stringify(body),
    }));
  }

  async function validAccessToken() {
    if (!session?.refresh_token) throw new Error("Sign in first.");
    if (session.access_token && Number(session.expires_at) > Date.now() / 1000 + 90) return session.access_token;
    const refreshed = await authRequest("token?grant_type=refresh_token", { refresh_token: session.refresh_token });
    saveSession({ ...refreshed, expires_at: Math.floor(Date.now() / 1000) + refreshed.expires_in });
    return session.access_token;
  }

  async function authorizedFetch(path, options = {}) {
    const token = await validAccessToken();
    const response = await fetch(`${config.supabaseUrl}${path}`, {
      ...options,
      headers: { ...apiHeaders, Authorization: `Bearer ${token}`, ...(options.headers || {}) },
    });
    return parseResponse(response);
  }

  async function licenseRequest(action, body) {
    return authorizedFetch(`/functions/v1/license-api/${action}`, {
      method: body === undefined ? "GET" : "POST",
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  }

  function setBusy(busy) {
    document.querySelectorAll("button").forEach((button) => {
      button.disabled = busy || (button.matches("[data-buy]") && !checkoutAvailable);
    });
  }

  function formatDate(value) {
    return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
  }

  function setAuthMode(mode) {
    authMode = mode;
    document.querySelectorAll("[data-auth-tab]").forEach((button) => {
      button.classList.toggle("active", button.dataset.authTab === mode);
    });
    elements.passwordRow.hidden = mode === "reset";
    elements.password.required = mode !== "reset";
    elements.password.autocomplete = mode === "signup" ? "new-password" : "current-password";
    elements.authSubmit.textContent = mode === "signin" ? "Sign in" : mode === "signup" ? "Create account" : "Send reset link";
    elements.authHelp.textContent = mode === "signin" ? "Use the account connected to your Tbot license."
      : mode === "signup" ? "We will email you a verification link before the account can be used."
      : "We will email you a secure link to choose a new password.";
    clearNotice();
  }

  async function loadDashboard() {
    setBusy(true);
    try {
      const [user, status, copies, purchases] = await Promise.all([
        authorizedFetch("/auth/v1/user"), licenseRequest("status"),
        authorizedFetch("/rest/v1/license_copies?select=copy_id,label,registered_at&revoked_at=is.null&order=registered_at.asc"),
        authorizedFetch("/rest/v1/license_purchases?select=checkout_session_id,months,amount_eur_cents,paid_at,refunded_at&order=paid_at.desc"),
      ]);
      elements.accountEmail.textContent = user.email || "Tbot account";
      renderStatus(status);
      renderCopies(copies);
      renderPurchases(purchases);
      elements.authPanel.hidden = true;
      elements.recoveryPanel.hidden = true;
      elements.dashboard.hidden = false;
    } catch (error) {
      showNotice(error.message, true);
      if (/session|token|sign in/i.test(error.message)) {
        saveSession(null);
        elements.authPanel.hidden = false;
        elements.dashboard.hidden = true;
      }
    } finally { setBusy(false); }
  }

  function renderStatus(status) {
    checkoutAvailable = config.checkoutEnabled && status.checkout_enabled === true;
    document.querySelectorAll("[data-buy]").forEach((button) => {
      button.disabled = !checkoutAvailable;
      button.title = checkoutAvailable ? "" : "Payments are not open yet.";
    });

    if (status.tier === "pending") {
      elements.licenseTier.textContent = "Ready to start";
      elements.licenseDetail.textContent = status.pending_paid_months > 0
          ? `Your ${status.pending_paid_months} prepaid month${status.pending_paid_months === 1 ? "" : "s"} begin when automation first starts.`
        : status.promo_activation_available
          ? "Your three free months begin when automation first starts."
        : status.promo_redeemed
          ? "The activation window for your three-month code has expired."
        : "Your three-day trial begins when automation first starts.";
    } else if (status.tier === "full" && status.lifetime_access) {
      elements.licenseTier.textContent = "Lifetime access";
      elements.licenseDetail.textContent = "Your account has permanent full access.";
    } else if (status.tier === "full") {
      elements.licenseTier.textContent = "Full access";
      elements.licenseDetail.textContent = `Available until ${formatDate(status.full_access_until)}.`;
    } else {
      const hours = Math.floor(status.seconds_remaining / 3600);
      const minutes = Math.floor((status.seconds_remaining % 3600) / 60);
      elements.licenseTier.textContent = `${hours}h ${minutes}m left today`;
      elements.licenseDetail.textContent = `Free time resets at ${formatDate(status.next_reset_at)}.`;
    }
  }

  function renderCopies(copies) {
    elements.copyCount.textContent = `${copies.length} of 2 registered`;
    elements.copies.replaceChildren();
    if (!copies.length) {
      const empty = document.createElement("p"); empty.className = "empty";
      empty.textContent = "No copies registered yet. Sign in from the program to register one.";
      elements.copies.append(empty); return;
    }
    copies.forEach((copy) => {
      const row = document.createElement("div"); row.className = "copy-row";
      const info = document.createElement("div");
      const name = document.createElement("strong"); name.textContent = copy.label;
      const date = document.createElement("small"); date.textContent = `Registered ${formatDate(copy.registered_at)}`;
      const remove = document.createElement("button"); remove.type = "button"; remove.textContent = "Remove";
      remove.addEventListener("click", () => removeCopy(copy.copy_id));
      info.append(name, date); row.append(info, remove); elements.copies.append(row);
    });
  }

  function renderPurchases(purchases) {
    elements.purchases.replaceChildren();
    if (!purchases.length) {
      const empty = document.createElement("p"); empty.className = "empty";
      empty.textContent = "No purchases yet."; elements.purchases.append(empty); return;
    }
    purchases.forEach((purchase) => {
      const row = document.createElement("div"); row.className = "purchase-row";
      const info = document.createElement("div");
      const name = document.createElement("strong"); name.textContent = `${purchase.months} month${purchase.months === 1 ? "" : "s"}`;
      const date = document.createElement("small"); date.textContent = formatDate(purchase.paid_at);
      const amount = document.createElement("div");
      amount.textContent = purchase.refunded_at ? "Refunded" : `€${(purchase.amount_eur_cents / 100).toFixed(2)}`;
      info.append(name, date); row.append(info, amount); elements.purchases.append(row);
    });
  }

  async function removeCopy(copyId) {
    if (!window.confirm("Remove this program copy from your account? Automation in that copy will stop.")) return;
    setBusy(true); clearNotice();
    try { await licenseRequest("remove-copy", { copy_id: copyId }); await loadDashboard(); showNotice("Program copy removed."); }
    catch (error) { showNotice(error.message, true); }
    finally { setBusy(false); }
  }

  document.querySelectorAll("[data-auth-tab]").forEach((button) => {
    button.addEventListener("click", () => setAuthMode(button.dataset.authTab));
  });

  elements.authForm.addEventListener("submit", async (event) => {
    event.preventDefault(); setBusy(true); clearNotice();
    try {
      const email = elements.email.value.trim();
      if (authMode === "reset") {
        const redirect = `${location.origin}${location.pathname}`;
        await authRequest(`recover?redirect_to=${encodeURIComponent(redirect)}`, { email });
        showNotice("Check your email for the password reset link.");
      } else if (authMode === "signup") {
        const redirect = `${location.origin}${location.pathname}`;
        await authRequest(`signup?redirect_to=${encodeURIComponent(redirect)}`,
          { email, password: elements.password.value });
        showNotice("Account created. Check your email to verify it, then sign in.");
      } else {
        const result = await authRequest("token?grant_type=password", { email, password: elements.password.value });
        saveSession({ ...result, expires_at: Math.floor(Date.now() / 1000) + result.expires_in });
        elements.password.value = "";
        await loadDashboard();
      }
    } catch (error) { showNotice(error.message, true); }
    finally { setBusy(false); }
  });

  elements.recoveryForm.addEventListener("submit", async (event) => {
    event.preventDefault(); setBusy(true); clearNotice();
    try {
      const token = await validAccessToken();
      await parseResponse(await fetch(`${config.supabaseUrl}/auth/v1/user`, {
        method: "PUT", headers: { ...apiHeaders, Authorization: `Bearer ${token}` },
        body: JSON.stringify({ password: elements.newPassword.value }),
      }));
      history.replaceState({}, document.title, location.pathname);
      showNotice("Password updated."); await loadDashboard();
    } catch (error) { showNotice(error.message, true); }
    finally { setBusy(false); }
  });

  document.querySelector("#signout").addEventListener("click", async () => {
    setBusy(true);
    try {
      const token = await validAccessToken();
      await fetch(`${config.supabaseUrl}/auth/v1/logout`, { method: "POST", headers: { ...apiHeaders, Authorization: `Bearer ${token}` } });
    } catch { /* Local sign-out must still succeed if the service is unavailable. */ }
    saveSession(null); elements.dashboard.hidden = true; elements.authPanel.hidden = false;
    setAuthMode("signin"); showNotice("Signed out."); setBusy(false);
  });

  document.querySelector("#refresh-status").addEventListener("click", loadDashboard);
  document.querySelector("#promo-form").addEventListener("submit", async (event) => {
    event.preventDefault(); setBusy(true); clearNotice();
    try {
      const input = document.querySelector("#promo-code");
      await licenseRequest("redeem", { code: input.value.trim() }); input.value = "";
      await loadDashboard(); showNotice("Code redeemed. Its access period starts with your first automation run.");
    } catch (error) { showNotice(error.message, true); }
    finally { setBusy(false); }
  });
  document.querySelectorAll("[data-buy]").forEach((button) => {
    button.addEventListener("click", async () => {
      if (!config.checkoutEnabled) return;
      setBusy(true); clearNotice();
      try {
        const result = await licenseRequest("checkout", { months: Number(button.dataset.buy) });
        location.assign(result.url);
      } catch (error) { showNotice(error.message, true); setBusy(false); }
    });
  });

  const hash = new URLSearchParams(location.hash.slice(1));
  if (hash.get("access_token") && hash.get("refresh_token")) {
    saveSession({ access_token: hash.get("access_token"), refresh_token: hash.get("refresh_token"),
      expires_at: Math.floor(Date.now() / 1000) + Number(hash.get("expires_in") || 3600) });
    history.replaceState({}, document.title, location.pathname);
    if (hash.get("type") === "recovery") {
      elements.authPanel.hidden = true; elements.recoveryPanel.hidden = false;
    } else {
      loadDashboard();
    }
  } else if (hash.get("error_description")) {
    history.replaceState({}, document.title, location.pathname);
    showNotice(hash.get("error_description"), true);
  } else if (session) {
    loadDashboard();
  }

  const result = new URLSearchParams(location.search).get("checkout");
  if (result === "success") showNotice("Payment received. Refresh if the new end date is still being processed.");
  if (result === "cancel") showNotice("Checkout was canceled.");
})();
