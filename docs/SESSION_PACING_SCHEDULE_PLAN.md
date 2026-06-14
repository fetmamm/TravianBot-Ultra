# Session Pacing Schedule Plan

1. Extend Session pacing with 24 hourly checkboxes where all hours are enabled by default and disabled hours trigger logout and sleep.
2. Use the computer's local time and randomize each schedule boundary using the existing `Variation %`, with a default of `±30%` of one hour.
3. Add a maximum daily runtime dropdown with `No limit` and `1–24 hours`, where `No limit` preserves the current behavior.
4. Apply the same `±%` variation to the selected daily runtime limit, for example `12 hours ±30%`.
5. Store the settings, accumulated runtime for the current day, and randomized limits per account so an application restart does not reset them.
6. Reuse the existing controlled stop, logout, sleep, and wake flow for disabled hours and reached daily runtime limits.
7. Reset the normal Session pacing run timer after a scheduled wake or application start so a long scheduled sleep is not immediately followed by regular pacing sleep.
8. Reset the daily runtime limit at local midnight and resume only when the next enabled hour begins.
9. Add tests for hourly boundaries, variation, midnight rollover, application restart, daily limits, and interaction with normal Session pacing.
10. Update `README.md` and `docs/ENGINEERING_NOTES.md` when the feature is implemented.
