---
status: accepted
---

# Deep continuous automation orchestration

Continuous Loop and Auto Queue share one deep Desktop orchestration module, `AutomationDesk`. Its caller-first interface starts, stops, wakes, and answers typed decisions; an internal mailbox serializes Automation Pass transitions, while `LoopController` remains the sole owner of lifecycle and cancellation and Worker retains Official Travian browser actions behind an adapter. The `MainWindow` composition root constructs the desk's internal runtime modules and mode passes, but the window retains only the desk. This concentrates scheduling, batching, pacing, deadlines, retries, Village Status Round behavior, runtime state, and generation safety behind one seam so callers and scenario tests use the same test surface.

## Considered options

- A duplex event stream maximized depth but leaked channel and event-consumption discipline to callers.
- A configurable run actor supported hypothetical modes but exposed more profile and protocol interface than current needs justify.
- The selected caller-first interface keeps the common Desktop caller trivial while preserving typed, atomic automation updates.

## Consequences

The module remains WPF-independent, binds each run to an immutable account/server/browser generation, coalesces wake requests, and keeps runtime state ephemeral. Migration must be staged and behavior-preserving; existing browser sequences, queue ordering, authoritative deadlines, and `LoopController` ownership do not change.
