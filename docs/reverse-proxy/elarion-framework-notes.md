# Elarion notes (from the reverse-proxy design)

Observations from designing the reverse-proxy feature. **Corrected after verifying against the actual
referenced packages** (`0.2.3-preview.79.1`) and after author feedback: most of my initial "framework
asks" were wrong — the capabilities already exist in Elarion; Watchtower is simply pinned to an older
preview and hasn't adopted them. So this is now mostly a **Watchtower adoption/upgrade list**, with only
a couple of genuine framework-level DX items at the end.

Verification performed:
- `[Service]` / `ServiceAttribute` **is present** in `elarion.abstractions/0.2.3-preview.79.1`.
- `Elarion.Streams` / `Elarion.Actors` are **not referenced and not in the local NuGet cache** at
  `0.2.3-preview.79.1`; `elarion.aspnetcore/0.2.3-preview.79.1` shows **no** `MapElarionClientEvents` /
  `IClientEventPublisher`. `StreamHub`/`MapElarionStream` appear only in newer `schema-test` package
  READMEs — i.e. they exist in *later* Elarion, not the pinned one.

---

## A. Watchtower adoption items (not framework gaps)

### A1. Adopt `StreamHub` / `MapElarionStream` for server→client streaming — after bumping Elarion
Watchtower hand-rolls SSE three times (deploy output, container logs, planned route/cert status): a
singleton `DeployOutputBroadcaster` (history + `Channel<T>` fan-out), a minimal-API `text/event-stream`
route in `WatchtowerHttpEndpoints.cs`, and a raw `EventSource` on the client. **Elarion already provides
the ordered/resumable, non-actor stream primitive** (`Elarion.Streams.StreamHub<T>` + `MapElarionStream`)
in current versions — my earlier claim that it "requires an actor" was wrong. It's just not in the pinned
`0.2.3-preview.79.1` (no `Elarion.Streams` reference).

Action: bump Elarion, add `Elarion.Streams`, and migrate the three bespoke broadcasters to
`StreamHub` + `MapElarionStream` (typed, resumable via `Last-Event-ID`). Until then, this feature uses
**polling** for the low-frequency route/cert status (see plan §10) rather than adding a fourth
broadcaster.

### A2. Adopt `[Service]` for the hosted-singleton / background-worker lifecycles
`[Service]` is supported at the pinned version, yet there is **zero** `[Service]` usage — every service
is hand-registered in `WatchtowerServiceCollectionExtensions`, including the two recurring shapes
(singleton-that-is-also-`IHostedService` like `DeployQueueService`; `BackgroundService` +
`IOptionsMonitor` like `StackUpdateBackgroundService`). This was the codebase choice, not a framework
limitation. Worth a pass to move these onto `[Service]`/the framework's hosted-service support so the
"register nothing" model actually applies. (New `CaddyManager` in this feature follows the **existing**
manual-DI convention for consistency; revisit alongside the wider migration.)

### A3. Consider adopting client events for status *hints* — after bumping Elarion
For "cert/route status changed → re-query," client events (`IClientEvent` + typed `events-client.ts`)
are the right tool (distinct from streaming logs, A1). Not present in the pinned aspnetcore package;
becomes available on the same bump. This is the cleaner long-term home for the status updates that this
feature currently polls for.

## B. Genuine framework-level DX items

### B1. `--export-schema <relative path>` resolves against the process CWD — silent staleness
`Program.cs` does `File.WriteAllText(path, …)` verbatim; the toolchain assumes `rpc-schema.json` at the
repo root (frontend reads `../../rpc-schema.json`, Dockerfile/CI use root). The trap is worse than "run
it from the wrong directory": `dotnet run --project src/Watchtower.Api` runs the app with its CWD set to
the **project directory** (via launchSettings), so a bare relative `rpc-schema.json` lands in
`src/Watchtower.Api/` *regardless of where you invoke the command from*. The frontend then regenerates
against the **stale committed** schema and nothing complains.

CI itself used to hide this: the freshness step exported to a relative path (→ project dir) and then
`git diff`ed the repo-root file it never touched, so the check passed on any drift — a vacuous gate.
Fixed by exporting to an **absolute repo-root path** (`--export-schema "$PWD/rpc-schema.json"`), both in
CI (`.github/workflows/ci.yml`) and in every doc that shows the command (WI-6). An absolute path
sidesteps the CWD entirely.

Request (still open, framework side): resolve a relative output against a stable anchor (git/solution
root or nearest `Directory.Build.props`), refuse a bare relative path, or print a prominent warning with
the absolute resolved path vs. the last committed location — so the safe invocation is the default.

### B2. (Low priority) Opt-in EF conventions for common SQLite value shapes
The EF configs repeat SQLite boilerplate: enums via `HasConversion<string>()` on every property, a
`string[]`-as-newline-text converter + custom `ValueComparer`, and **client-side** `OrderByDescending`
because "SQLite can't ORDER BY a DateTimeOffset." Since Elarion already owns the snake-case convention +
EF generator, opt-in conventions (enum-as-string default, a canonical `string[]` converter, a sortable
`DateTimeOffset` storage convention for SQLite) would remove copy-pasted code from every SQLite Elarion
app. EF/SQLite realities, not bugs — just a convenience.

## C. Forward-looking (no action)
When Watchtower becomes the reverse proxy, it's the natural place to also terminate auth (Caddy
forward-auth/basic-auth, or a real login). At that point Elarion's `[RequirePermission]`/`[RequireRole]`
+ a real `ICurrentUser` + the session capability model light up, and the frontend contribution
`when: { permission }` gates (only `when: { module }` is used today) start carrying weight. The
machinery is already there for the day this feature makes auth relevant.

---

### Summary
Corrected takeaway: the two big things I first flagged as missing (non-actor streaming, `[Service]`
background lifecycles) **already exist in Elarion** — Watchtower is pinned to `0.2.3-preview.79.1` and
hasn't adopted them (A1–A3, gated on a version bump for streaming/client-events). The only real
framework-level papercut is **B1** (schema-export path resolution). This feature is implemented on the
pinned version using the codebase's existing conventions; the Elarion bump + adoption is a clean,
separate follow-up.
