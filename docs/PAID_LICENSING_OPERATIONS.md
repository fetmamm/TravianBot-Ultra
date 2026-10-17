# License launch operations

This is the deployment companion to [PAID_LICENSING_PLAN.md](PAID_LICENSING_PLAN.md). Keep secret values in Supabase and GitHub settings, never in the repository or Discord.

## 1. Supabase

The desktop bundle already contains the project's public URL and publishable key in `.release-template/config/license.json`. The project reference is `sfgyrrduvbyxywahsjil`.

Current deployment state (2026-10-15): the project is linked, migrations `202610150001` through `202610150003` are applied, both Edge Functions are deployed, email confirmation is enabled, and the remote transactional license test passes. The test covers two copies, two concurrent runs, trial machine reuse, daily quota accounting, promotion activation and expiry, duplicate payments, reversal, and lifetime access; its rollback has been verified. The owner account has an active, auditable lifetime grant. `license_configuration.launched_at` is still `NULL`, so the existing-user redemption window has not started.

1. The Supabase CLI is signed in and this repository is linked to the project. Migration `supabase/migrations/202610150001_license_core.sql` has been applied.
2. Auth requires email confirmation. The Pages site and account redirect are configured. Custom SMTP is enabled with `Tbot Ultra <tbotultra@gmail.com>` through `smtp.gmail.com` on port 465; its Google app password is stored only in Supabase. On 2026-10-16, both a password-recovery email and a new-account confirmation email were delivered with the `Tbot Ultra` sender name. Supabase warns that Gmail is a personal-mail provider, so monitor deliverability and move to a transactional provider if delivery becomes unreliable.
3. `license-api` is deployed with JWT verification on and `stripe-webhook` with JWT verification off. Only the Stripe webhook may receive unauthenticated requests; its Stripe signature is checked in code.
4. Set `SITE_ORIGIN`, `CHECKOUT_SUCCESS_URL`, and `CHECKOUT_CANCEL_URL` for the website. Set the Stripe secrets and four price IDs in Supabase Edge Function secrets after the Stripe account is ready. `CHECKOUT_ENABLED=true` is currently allowed only with the sandbox secrets for continued testing. At production cutover, replace every Stripe value as one set before keeping it enabled. The server defaults to disabled if the secret is missing. Never place `SUPABASE_SERVICE_ROLE_KEY` or `STRIPE_SECRET_KEY` in a client or a GitHub release.
5. Set the launch time once, with an administrator database connection: `update public.license_configuration set launched_at = now(), updated_at = now() where id = true;`. Do this only at launch; it starts the 30-day code redemption window.

## 2. Existing-user codes

Run `scripts/New-LicensePromoCodes.ps1 -Count 50` near launch. It writes `artifacts/license-promo/discord-codes.csv` and `insert-code-hashes.sql`. The directory is ignored by Git. Import the hash-only SQL file with an administrator connection after setting the launch time. Add each Discord recipient to the CSV locally, then send one unique code privately to each existing user. Do not publish or commit the CSV.

Redeeming a code does not start the three months. The first automation start after redemption starts the clock, provided it happens within 30 days of launch. The time continues while the program is closed.

## 3. Stripe

Create a Stripe account and ask Stripe whether this software and account qualify for Managed Payments. Create four **one-time EUR prices with tax included**: 1 month €2.99, 2 months €4.99, 6 months €13.99, 12 months €24.99. Enter their price IDs as `STRIPE_PRICE_1_MONTH`, `STRIPE_PRICE_2_MONTHS`, `STRIPE_PRICE_6_MONTHS`, and `STRIPE_PRICE_12_MONTHS`.

Current sandbox state (2026-10-16): all four products and one-time EUR prices exist and show as eligible for Managed Payments. Their four Price IDs, a rotated sandbox `STRIPE_SECRET_KEY`, and `STRIPE_WEBHOOK_SECRET` are present in Supabase; secret values were not read back. A completed Checkout test produced exactly one purchase, and resending the same event kept exactly one purchase and one paid period, proving live webhook idempotency. A second two-month/€4.99 purchase followed by a full refund delivered `refund.created` and marked that exact purchase refunded. The earlier refund, made before that event subscription existed, was reconciled once with an audit event. Both earlier sandbox purchases are marked refunded. Leaving Checkout without paying created no purchase or completion event. A separate six-month/€13.99 Checkout completed successfully and is currently the only active paid purchase; the owner's lifetime grant remains active independently.

Launch hold (2026-10-16): keep Stripe, Supabase Checkout secrets, and the website in sandbox mode while the remaining website and desktop changes are being made. Sandbox Checkout may stay enabled for test purchases, but do not replace its credentials with live credentials. Do not publish the transition release, enable live checkout, set `launched_at`, distribute promotion codes, or make the source repository private until those changes have passed a new full verification round and the owner explicitly resumes launch preparation.

Configure a webhook to `https://sfgyrrduvbyxywahsjil.supabase.co/functions/v1/stripe-webhook` for `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `charge.refunded`, `refund.created`, and `charge.dispute.created`. Put its signing secret in `STRIPE_WEBHOOK_SECRET`. Use separate test and live secrets. The webhook only grants time after Stripe says the Checkout session is paid and its EUR total matches the selected package.

For sandbox setup, run `scripts/Configure-StripeSandbox.ps1` and paste the `sk_test_...` key at its hidden prompt. It creates or deliberately recreates only the matching Tbot sandbox webhook, captures the one-time signing secret, sends both secrets to Supabase through a temporary ignored file, and deletes that file immediately. Never paste the key into chat or command-line arguments.

Before live sales, complete Stripe's account verification and test one purchase, duplicate webhook delivery, refund, and an interrupted payment. Confirm exact end dates in the app and website. Publish purchase terms and a privacy notice before accepting payments.

Public product contact selected on 2026-10-16: Jesper Svensson, 544 32 Hjo, Sweden, `tbotultra@gmail.com`. The website has Terms, Privacy, and Contact pages using those details. The owner does not want a street address or telephone number published. Confirm the final disclosure requirements with Stripe Managed Payments before enabling live checkout. Gmail forwarding from `tbotultra@gmail.com` to `jesperr.svensson@gmail.com` is active. The private account can send as `Tbot Ultra <tbotultra@gmail.com>`, and replies use the same address that received the message. Two-step verification is enabled for the Tbot Gmail account, and a dedicated Google app password is stored in Supabase custom SMTP.
Keep `website/config.js` in `checkoutMode: "sandbox"` during development. Before the live deployment, replace all Stripe and Supabase Checkout secrets together, change the mode to `live`, and repeat the full payment verification; never use sandbox price IDs with a live key or vice versa.

## 4. GitHub distribution

The established [fetmamm/TravianBot-Ultra](https://github.com/fetmamm/TravianBot-Ultra) repository remains public and becomes the binary release repository. Keeping this repository identity preserves its stars, release URLs, Discord links, and the update endpoint used by installed versions. `.release-template/config/update.json`, the website download link, the app support link, and the default update checker all point to it.

The empty public `fetmamm/Tbot-Ultra-Releases` repository is repurposed as private `fetmamm/Tbot-Ultra-Source`. Mirror every source branch and tag there and verify the private copy before changing public history. GitHub Actions then runs in the private repository and publishes the portable zip and SHA256 asset to `fetmamm/TravianBot-Ultra`.

After the private mirror and release workflow are verified, replace every public source branch and source-bearing tag with a new release-only history. Keep a minimal `main` branch with download instructions and release metadata. This makes normal browsing and new clones source-free while retaining the public repository and its stars. It cannot remove source already present in external clones or detached public forks, and old Git objects or pull-request references may remain reachable.

Version `0.7.9` is prepared but must be rebuilt after the 2026-10-17 desktop changes. The earlier assembled portable folder and extracted zip passed metadata, structure, browser, startup, and test checks before those changes. Publish `0.7.9` to `fetmamm/TravianBot-Ultra`; both old and new installations continue checking that same release endpoint, so no repository transition release is required.

In `fetmamm/Tbot-Ultra-Source`, set GitHub Actions variable `PUBLIC_RELEASE_REPOSITORY` to `fetmamm/TravianBot-Ultra`. Add `RELEASE_REPO_TOKEN` as a fine-grained token with **Contents: read and write** on only the public repository, and recreate `DISCORD_ANNOUNCEMENT_WEBHOOK_URL` because Actions secrets do not move with Git history. Verify that a release publishes to the public repository, the Discord message links to that release, and the application's update button opens the same public release.

Migration order:

1. Rename `Tbot-Ultra-Releases` to `Tbot-Ultra-Source` and make it private.
2. Mirror all source branches and tags to it and verify commit IDs and repository visibility.
3. Recreate the Actions secrets and set `PUBLIC_RELEASE_REPOSITORY`.
4. Add the Cloudflare Pages credential to the private repository, enable `.github/workflows/deploy-website.yml`, and verify a sandbox deployment to the existing `tbot-ultra` project.
5. Create and validate a release-only public history from `.public-release-template/`.
6. After explicit final approval, replace public branches and tags, then publish and verify `0.7.9`.
7. Keep Stripe and the website in sandbox mode throughout this migration; production payment cutover remains the final launch change.

Current migration state (2026-10-17): steps 1 and 2 are complete. `fetmamm/Tbot-Ultra-Source` is private and its `main`, `V0.6.2`, `V0.6.5`, and `V0.7.9` heads match the corresponding source commits; all version tags were copied. `PUBLIC_RELEASE_REPOSITORY=fetmamm/TravianBot-Ultra` is configured. The private repository still needs `RELEASE_REPO_TOKEN`, `DISCORD_ANNOUNCEMENT_WEBHOOK_URL`, and `CLOUDFLARE_API_TOKEN`. Set `CLOUDFLARE_ACCOUNT_ID` to the Pages account ID and keep `CLOUDFLARE_PAGES_DEPLOY_ENABLED=false` until the token exists. The existing Git-integrated Pages project cannot be relinked to another repository without recreation, so the private workflow uses Wrangler Direct Upload to retain the current `tbot-ultra.pages.dev` project and address. The public repository remains source-bearing until the final cutover and retains 12 stars, six detached-capable public forks, its issues, and releases.

For the Cloudflare token, create a scoped API token with **Account / Cloudflare Pages / Edit** for the account that owns `tbot-ultra`. Store it only as the private repository secret `CLOUDFLARE_API_TOKEN`. After setting the account ID variable, enable `CLOUDFLARE_PAGES_DEPLOY_ENABLED=true`, run the workflow manually once, and verify that `/config.js` still says `checkoutMode: "sandbox"` and downloads point to `fetmamm/TravianBot-Ultra`.

`scripts/Invoke-PublicReleaseCutover.ps1` audits repository visibility, stars, required secrets, variables, the deployed sandbox website, and the prepared release-only tree. Run it without parameters for a read-only readiness report. Its `-Apply` path refuses to run without the exact confirmation phrase documented by the script; it then force-replaces public `main`, removes source branches, and moves existing release tags to the release-only root so ordinary tag archives no longer contain source.

Users download the complete portable zip from the public repository's Releases page. They extract it into a writable folder and sign in to their Tbot account. A person may register two separate folders, including both on the same computer; two instances must not share one folder.

## 5. Website, then launch

The Cloudflare Pages website is the last implementation stage. It serves the public download/pricing page and account pages using the same Supabase project and Stripe Checkout flow. Set the website origin and Checkout return URLs before enabling purchases. The initial launch may use `pages.dev`; a custom domain can be connected later.

Current deployment state (2026-10-16): the Pages project is `tbot-ultra`, `https://tbot-ultra.pages.dev` returns the site successfully, and the origin plus Checkout success/cancel URLs are configured as Supabase Edge Function secrets. Supabase Auth uses this address as its Site URL and allows the exact `/account.html` redirect; the remote configuration was verified after push. Purchase, duplicate-delivery, refund, and cancellation checks passed in Stripe sandbox. Checkout is enabled with sandbox credentials for continued testing; it must remain in sandbox mode until the launch hold is explicitly lifted.

Before live checkout, add the deployed Terms and Privacy URLs in Stripe Checkout settings so they are presented in the Managed Payments flow.

Keep an off-service backup of account entitlements, purchases, code claims, and audit records. Supabase Free does not provide downloadable database backups by default. Record where the backup is stored and test a restore before taking live payments.

Run `scripts/Backup-LicenseData.ps1` to export the Auth identity map and every license ledger table to `artifacts/license-backup/`, together with a SHA256 file. Run `scripts/Test-LicenseBackup.ps1` immediately afterwards; it verifies the checksum, required tables, Auth references, package prices, and two-copy invariant. The latest verified sandbox snapshot is `tbot-license-20261016-052104Z.json`; it contains one user, three purchases, and one lifetime grant. The directory is ignored by Git and contains personal data. Copy each launch backup to encrypted storage outside Supabase and the source checkout. A restore must map the saved email addresses to the restored Auth users before inserting dependent license rows; rehearse that procedure in a separate Supabase project before live sales.
