# City capability and Watchtower queue

## Decision

- Model server support as `CityCapability` (`Enabled`, `Disabled`, `Unknown`) and each village as `CityStatus` (`City`, `Village`, `Unknown`). Unknown is never treated as affirmative eligibility.
- Read `T4_feature_flags.cities` after login. Profile rows may identify Cities globally; a complete live Dorf2 overview is authoritative for the active village. Incomplete overviews remain `Unknown`.
- Ordinary villages use slots 19–38. Only a confirmed City may use slots 41–43. Slots 39 and 40 remain Rally Point and wall.
- Watchtowers are a wall extension with their own two-place queue. They do not consume or block the ordinary resource/building construction lanes.
- A Watchtower task requires confirmed Cities capability, confirmed City status, an existing wall, and known wall-extension status. Invalid saved tasks stay pending and dormant without navigation; a later verified status automatically makes them runnable.
- Waterworks remains an Egyptian building requiring Hero's Mansion 10. On Cities-enabled servers it additionally requires a confirmed City. Unknown server capability is not a reason to pre-block it; the live build page remains final authority.

## Consequences

- Wall-page reads are demand-driven: first confirmed City discovery, explicit Buildings load, queued/active Watchtower work, or expired cache.
- Watchtower targets are supported manually and in building templates, but excluded from generic “Upgrade all to max”.
- Non-capital Cities may upgrade resource fields to level 12.
