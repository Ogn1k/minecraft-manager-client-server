# Launcher implementation status

This checklist tracks the implementation prompts in `promts/launcher_update`. Architecture and security invariants remain defined in `client-architecture.md` and `client-launcher-architecture.md`.

- [x] 00 — Baseline and guardrails
- [x] 01 — Instance domain and persistence migration
- [x] 02 — Shell navigation and shared selection
- [x] 03 — Runtime metadata resolution
- [x] 04 — Java runtime management
- [x] 05 — Launch engine and process monitoring
- [x] 06 — Launcher UI and settings
- [x] 07 — Offline profiles
- [x] 08 — Runtime provisioning and repair
- [x] 09 — Mod-loader runtime integration (Fabric/Quilt implemented; Forge/NeoForge fail closed)
- [x] 10 — Managed-server policy and hardening

Automated verification covers domain rules, migration, metadata resolution, argument construction, offline UUIDs, cache reuse, hostile native archives, process lifecycle, dependency boundaries, server contract constraints, and existing updater/server behavior. Real-Minecraft, browserless Linux keyring (managed-server credentials), native ARM64, and packaging smoke tests remain release-environment checks.
