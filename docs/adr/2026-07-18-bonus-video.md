# ADR: Bonus-video lifecycle and failure handling

## Status

Active decision, extracted from `ENGINEERING_NOTES.md` on 2026-07-18.

## Routing, timing and completion

- Bonus-video traffic uses the account's current route and proxy. Never bypass the proxy or change IP only
  for video.
- Isolated video has separate 60-second setup and 240-second action caps. Expected provider failure must not
  block construction, hero dispatch, or other automation.
- Construct, resource, production, and hero bonus videos share one post-play policy: the protected 60-second
  interval begins only after a trusted play click succeeds; post-play verification times out after 120 seconds.
- During the protected minute, missing iframe/dialog/reward or provider help/error text cannot end the attempt.
  Afterward, provider failure needs two consecutive confirmations while the player is present, or one when it
  is demonstrably absent. Cancellation, shutdown, and closed/crashed browser may abort immediately.

## Verified success exceptions

- Construct-faster may complete immediately after a redirect back to the village when the player is gone,
  but only after video activity was observed. A dialog merely opening is not a redirect. Dialog/player loss
  without a redirect still waits through the protected minute.
- Hero-adventure and production bonus may complete immediately when Travian's bonus box shows its active reward
  class/text and the video overlay has closed. If the overlay remains open, keep the protected minute.
- Construct-faster success also requires target-specific construction evidence: the exact slot/level is newly
  queued versus the pre-video snapshot, or has completed immediately.

## Failure classification and diagnostics

- Apply account+proxy cooldown by typed failure: network 10m, no-ad/cookies 20m, timeout 30m, stale isolated
  session 5m, missing codec 6h. Known failures receive no immediate second attempt.
- Preserve typed failure and cooldown deadline across features. Production bonus defers to that deadline
  without replacing saved timers or treating an unattempted video as a four-hour failure.
- Production-bonus inspection is complete only when the Advantages tab contains lumber, clay, iron, and crop.
  Retry empty/partial React rendering; after two 30-second attempts, raise a task failure.
- Diagnostics log only sanitized ad host, network error code, status, and aggregate counts—never paths,
  queries, credentials, cookies, or tokens.

## Consequences

Video failures are expected degraded states, normally warnings rather than alarms. Any new video feature must
reuse the shared post-play policy, failure type, cooldown, routing, and sanitized diagnostics.
