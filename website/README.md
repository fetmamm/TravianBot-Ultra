# Tbot Ultra website

Static Cloudflare Pages source. No build command is required; set the Pages output directory to `website` when connecting this private repository.

Current sandbox deployment: `https://tbot-ultra.pages.dev`. It was created as the Cloudflare Pages project `tbot-ultra`; sandbox checkout was tested successfully and is disabled again until production setup is complete.

Before deployment:

1. Confirm `config.js` points to the public binary-only GitHub Releases URL.
2. Configure the exact Pages origin as `SITE_ORIGIN` for the Supabase `license-api` Edge Function. The current `pages.dev` origin is already configured.
3. Configure Supabase Auth site/redirect URLs for `account.html`.
4. Set Stripe Checkout success URL to `https://<host>/account.html?checkout=success` and cancel URL to `https://<host>/account.html?checkout=cancel`.
5. Enable `checkoutEnabled` only while running an intentional sandbox test or after production Stripe setup is complete.
6. A custom domain is optional for the first launch and can be connected later.

The Supabase URL and publishable key in `config.js` are intentionally public client configuration. Never add service-role or Stripe secret keys here.
