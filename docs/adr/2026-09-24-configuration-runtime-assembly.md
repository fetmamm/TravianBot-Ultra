---
status: accepted
---

# Domain-owned configuration-to-runtime assembly

`BotOptionsFactory`, `BotOptionsPayloadApplier`, and `SettingsConfigurationProjection` remain the stable public entry points, but they only orchestrate domain option modules. Each existing domain module owns its configuration defaults, compatibility rules, normalization, task-payload overlay, Settings draft projection, and projection back to the flat `BotOptions` compatibility record.

## Considered options

- A generic descriptor/reflection engine reduced mapping code but hid domain rules behind weakly typed metadata.
- A second public configuration service added another forwarding seam without reducing caller knowledge.
- Replacing `BotOptions` with nested options in one migration would force changes across Desktop and Worker simultaneously.

## Consequences

New domain settings belong in their domain option module instead of duplicating rules in public facades or WPF. Each module also declares its account-scoped keys; `AccountConfigurationScope` composes those declarations for Desktop persistence. `BotOptions` remains flat for existing callers, `BotConfigStore` retains persistence and migration ownership, and compatibility aliases stay explicit and covered by observable factory/payload and headless Settings-projection tests.
