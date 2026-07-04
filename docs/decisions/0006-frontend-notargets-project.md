# ADR-0006: The web frontend is a NoTargets project in the solution

- Status: Accepted
- Date: 2026-07-04
- Related: [ADR-0005](0005-aspire-dev-orchestration.md) (how the frontend is run in development).

## Context

The Vite + React SPA (`watchtower-web`) lived entirely outside the .NET solution — invisible in the IDE's
solution view and ungrouped with the projects it ships alongside. The Aspire AppHost
([ADR-0005](0005-aspire-dev-orchestration.md)) references it by path, but that does not make it a solution
member.

Elarion surfaces its own TypeScript package in its solution with a
[`Microsoft.Build.NoTargets`](https://github.com/microsoft/MSBuildSdks/tree/main/src/NoTargets) wrapper
project ([`elarion-contributions.csproj`](https://github.com/swimmesberger/Elarion/blob/main/src/elarion-contributions/elarion-contributions.csproj)):
a project that produces no .NET assembly but gives the npm/TypeScript package IDE visibility and an
optional MSBuild-driven build.

## Decision

**Wrap the SPA in a `Microsoft.Build.NoTargets` project (`watchtower-web.csproj`) and register it in the
solution.**

- The project sets `IsPackable=false`, `EnableDefaultItems=false`, and `ImplicitUsings=disable` (it is not
  a C# project), and lists the source/config files as `<None>` items for IDE visibility. It emits no
  assembly.
- The TypeScript/Vite build stays owned by npm. A `dotnet build` is **Node-free by default**; passing
  `-p:BuildJsClient=true` opts into MSBuild targets that run `npm ci` + `npm run build`.
- The Docker image and the Aspire AppHost are unaffected — they build/run the SPA through npm/Vite as before.

## Consequences

- **The SPA is a first-class solution member** — visible, grouped, and navigable in the IDE alongside the
  backend, matching how Elarion (and now Swerp) surface their frontends.
- **`dotnet build` stays Node-free**, so CI and contributors without Node installed are unaffected; the JS
  build remains an explicit, opt-in step.
- **No behavior change** to how the app is built or shipped — this is purely solution/IDE ergonomics.

### Rejected alternatives

- **Solution-folder file references.** Shows a few files but does not create a project node, gives no
  grouping, and offers no build hook.
- **A real SPA/JS SDK project that always builds the frontend on `dotnet build`.** Couples every .NET build
  to Node being present; the opt-in `BuildJsClient` switch keeps that cost off by default.
