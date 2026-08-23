# ADR-0014: Environment variables win over runtime settings; pinned settings are read-only in the UI

- Status: Accepted
- Date: 2026-08-17
- Related: [ADR-0013](0013-sqlite-metrics-history.md) (the first runtime-switchable settings surface),
  [docs/central-auth/design.md](../central-auth/design.md) (the auth plane whose enablement this
  governs), [docs/reverse-proxy/README.md](../reverse-proxy/README.md) (the proxy whose enablement
  this makes runtime-switchable).

## Context

Watchtower's runtime-editable settings live in the Elarion settings store (`elarion_settings`) and are
layered into `IConfiguration` as a live-reloading provider, so `IOptionsMonitor<WatchtowerOptions>`
re-binds without a restart. The provider was **appended last**, which in `IConfiguration` means it won
over everything — including `WATCHTOWER__*` environment variables.

That inversion had real consequences:

- A compose file stopped being the truth about the deployment: any setting once edited in the UI
  silently shadowed the env var forever after.
- The natural break-glass — `WATCHTOWER__AUTH__ENABLED=false` + restart — would stop working the
  moment `Auth:Enabled` was stored in the database, precisely the situation where an operator is
  locked out and reaching for the environment.

Two further constraints shaped the fix:

- **`Auth:Enabled` is a pre-DI read.** `Program.cs` reads it before the host is built to decide
  middleware registration, endpoint mapping and the `ICurrentUser` implementation. The settings
  provider's data, however, is only pushed by a hosted service (`SettingsConfigurationRefresher`)
  *after* those reads — so a stored value could never influence the next start without extra work.
- A UI whose fields silently lose to the environment is worse than either precedence order; whichever
  layer wins, the operator must be able to see it.

## Decision

1. **Environment variables win over the settings store.** The settings source is repositioned below
   the environment-variable provider (`RuntimeSettingsLayering.MakeEnvironmentWin`), giving the order:
   `appsettings < boot snapshot < live settings store < env vars < command line`. Env vars are the
   infrastructure-as-code layer: what the compose file says is what runs.
2. **A synchronous boot snapshot carries stored settings to startup.** Before the host is built,
   `RuntimeSettingsLayering.LoadStoredGlobalSettings` reads the Global-scope rows straight from the
   SQLite file (read-only; tolerant of a missing file/table) into a memory source directly beneath the
   live provider. The pre-DI read — `Auth:Enabled` — therefore sees stored values, which
   is what lets a runtime-edited `Auth:Enabled` take effect on the next start.
3. **Pinned settings are visible, disabled, and write-rejected.** `EnvironmentSettingPins` maps
   `WATCHTOWER__X__Y` ⇄ `Watchtower:X:Y`. Every settings get-handler returns the pinned paths for its
   card (the UI disables those fields, per-field, with the variable named); every update handler
   rejects a request that would *change* a pinned value and never writes pinned keys (a stored row
   that cannot take effect is a lie waiting for the variable's removal).
4. **`Auth:Enabled` is restart-required, honestly.** The startup value is captured as
   `AuthStartupState`; the auth settings handlers report `restartRequired` whenever the configured
   value differs from the running pipeline. Enabling additionally requires an enabled admin account in
   the system realm, so the restart cannot land on a login page nobody can pass
   (`WATCHTOWER__AUTH__BOOTSTRAPPASSWORD` remains the alternative).
5. **`Proxy:Enabled` is fully runtime-switchable.** `CaddyManager` stops snapshotting its options and
   reacts to `IOptionsMonitor.OnChange`: disabled→enabled runs the full reconcile, enabled→disabled
   stops and removes the managed Caddy container (networks and the certificate volume are kept),
   other changes while enabled re-render the config. Transitions are serialized behind one lock.

## Consequences

- `WATCHTOWER__AUTH__ENABLED=false` + restart is again a guaranteed escape hatch, regardless of what
  the database says — no bespoke break-glass variable needed.
- **Behavior change:** a deployment that set an env var *and* later edited the same setting in the UI
  previously ran the UI value; it now runs the env value (and the UI shows the field as pinned).
  Noted in the release notes.
- The boot snapshot duplicates the live provider's data for a few hundred milliseconds at startup and
  goes stale immediately after; that is fine, because the live source sits directly above it.
- Every future runtime-editable setting must follow the pattern: constants in
  `WatchtowerSettingPaths`, pinned paths in the get-response, pin check + skip in the update handler.
- The settings UI can trust what it renders: an editable field is truly editable, a pinned field says
  which variable to remove.
