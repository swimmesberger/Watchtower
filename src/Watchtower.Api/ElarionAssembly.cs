using Elarion.AspNetCore;

// Opting in here makes the Elarion module-discovery generator emit this host's cross-module wiring as
// the fixed-name ElarionBootstrapper static in the root namespace (Watchtower.Api) — AddElarion,
// MapElarionEndpoints, RegisterHandlers, … — each feature-gated by Modules:{Name}:Enabled.
[assembly: GenerateModuleBootstrapper]
