# ADR: Bonus-video lifecycle and failure handling

## Status

Active decision, extracted from `ENGINEERING_NOTES.md` on 2026-07-18.

## Routing, timing and completion

- Bonus-video traffic uses the account's current route and proxy. Never bypass the proxy or change IP only
  for video.
- Isolated bonus-video Chrome starts minimized and reasserts that window state before video work. The normal
  Travian browser remains maximized, and a failed minimize confirmation must not abort the video attempt.
- Isolated video has separate 60-second setup and 240-second action caps. Expected provider failure must not
  block construction, hero dispatch, or other automation.
- Construct, resource, production, and hero bonus videos share one post-play policy: the protected 60-second
  interval begins only after verified active HTML-media autoplay or a trusted play click; post-play verification
  times out after 120 seconds.
- After the player appears, keep a 20-second pre-play observation window and continuously check for verified autoplay
  or a safe play control before classifying the attempt as unavailable.
- A trusted play click targets only the exact visible provider play control after ancestry, geometry, and center
  hit-testing. Never click the video-area or iframe center as fallback; during slow rendering that point may belong
  to an advertiser link and open an external tab.
- Optional muting supports both provider variants: set the media properties on a visible direct HTML video, or use
  the exact geometry-verified provider audio button when that older control-based player is rendered.
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
- The initial shared cooldown gate still applies before a production-bonus run starts. Once started, the four
  resources form one contiguous batch: a resource-specific failure may set the route cooldown for later tasks,
  but it does not prevent the remaining initially activatable resources from being attempted in this batch.
- Production-bonus inspection is complete only when the Advantages tab contains lumber, clay, iron, and crop.
  Retry empty/partial React rendering; after two 30-second attempts, raise a task failure.
- Diagnostics log only sanitized ad host, network error code, status, and aggregate counts—never paths,
  queries, credentials, cookies, or tokens.

## Consequences

Video failures are expected degraded states, normally warnings rather than alarms. Any new video feature must
reuse the shared post-play policy, failure type, cooldown, routing, and sanitized diagnostics.
