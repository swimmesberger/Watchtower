# ADR-0012: Injected variables reach containers directly, through a generated compose override

- Status: Accepted
- Date: 2026-08-10
- Related: [ADR-0008](0008-public-app-api.md) (the delivery mechanism this replaces),
  [ADR-0010](0010-target-kubesolo-runtime.md) (the dual-engine seam rule this follows),
  [ADR-0011](0011-user-scoped-tenant-discovery.md) (the feature that made zero-config delivery
  urgent).

## Context

[ADR-0008](0008-public-app-api.md) delivers the App API token through the deploy pipeline: Watchtower
writes `WATCHTOWER_APP_TOKEN`, `WATCHTOWER_STACK_ID` and the optional `WATCHTOWER_URL` into the temp
`.env` it hands to `docker compose --env-file`. That places the values *in the compose run* — and
compose only **interpolates** env-file variables. Unless the repository's compose file names them
itself, they never enter a container:

```yaml
environment:
  - "WATCHTOWER_APP_TOKEN=${WATCHTOWER_APP_TOKEN}"
```

Forgetting those lines is the App API's single largest footgun, and it fails in the worst possible
shape: the deploy is green, the containers are healthy, nothing in the deploy output is wrong, and
the application discovers at runtime that `WATCHTOWER_APP_TOKEN` is empty — so every call it makes
comes back `401`. The variable is *right there* in the env file the deploy used, which is exactly why
integrators conclude the token is broken rather than that a passthrough line is missing. The only
defence today is a warning box in the docs.

[ADR-0011](0011-user-scoped-tenant-discovery.md) raised the cost of that footgun. A tenant switcher
is a UI feature of the *product*, rendered per tenant, and it depends on the token being present in
whichever container serves the product's pages. Under the passthrough model a vendor shipping a
template has to get three passthrough lines right in a compose file they then instantiate for every
customer — and a mistake shows up as a switcher that silently never renders, on every tenant at once.

Watchtower does not merely hand compose an env file: it **owns the whole invocation**. It clones the
repository, assembles the `-f` / `--env-file` / project arguments, and runs `pull` and `up` itself.
Compose has a first-class mechanism for adding configuration to somebody else's compose file — a
second `-f` — and Watchtower is in the one position that can use it.

## Decision

**Watchtower generates a compose override file at deploy time and merges it into the deploy with a
second `-f`, placing the reserved `WATCHTOWER_*` variables directly into the `environment:` of the
services that should have them.** A repository's compose file no longer has to mention them.

- **An engine-neutral policy decides who gets what.** A pure component maps *service name →
  variables to inject*, computed from the stack (is it a tenant, and what is its template's target
  service), the compose services with their `watchtower.inject-token` label values, and which values
  exist. `WATCHTOWER_STACK_ID` goes to **every** service, always; `WATCHTOWER_URL` to every service
  whenever `Watchtower:PublicBaseUrl` is configured — the same condition as before.
- **The token is scoped, because the token is a credential.** It is the App API bearer, and on a
  stack holding a management grant ([ADR-0009](0009-public-management-api.md)) it can provision,
  redeploy and delete tenants. It is therefore never spread across a stack by default. Any service
  labelled `watchtower.inject-token: "true"` receives it; `"false"` never does, overriding every
  default. With no `"true"` label anywhere, exactly one default applies: a **tenant** stack gets it
  in its template's target service (when that service exists in the compose file), a **plain** stack
  with exactly one service gets it there, and a plain stack with several services gets it **nowhere**
  — Watchtower cannot guess which container is the application, so it says so instead of guessing.
- **Ambiguous input degrades to a warning, never to a guess.** A label value that is neither `"true"`
  nor `"false"` — matched case-insensitively, with surrounding whitespace ignored — is treated as
  absent, and a tenant whose template names a target service the compose file does not define gets no
  default injection. Both emit one warning line into the deploy output, which is where an integrator
  is already looking.
- **The rendering is Docker-private.** Services and labels are enumerated with
  `docker compose config --format json`, resolved with the same `--env-file` and project arguments as
  the deploy so it sees what the deploy will see. It is not the identical argv: `config` names only
  the repository's compose file, while `pull` and `up` carry a second `-f` for the override, and it
  does not need the registry-credential environment those two run with. A `config` failure fails the
  deploy with its output, as it would have at `pull` time anyway. The override YAML is written as a
  **sibling temp file** alongside the deploy's clone directory — not inside it — `0600` on Linux
  because it carries the token, and deleted individually at the end of the deploy, exactly like the
  generated `.env` next to it. It is appended **after** the repository's `-f` on the `pull` and `up`
  invocations, so compose merges it last and it wins per key.
- **Values are never printed.** The deploy output records one line per service naming the variables it
  received (`[Watchtower] Injecting WATCHTOWER_APP_TOKEN, WATCHTOWER_STACK_ID into service 'web'`).
  Service and variable ordering in the file is deterministic so the artifact is diffable.
- **The env-file mechanism is unchanged.** Interpolation still works, classic passthrough still
  works, and a repository that passes `${WATCHTOWER_APP_TOKEN}` through receives the identical value
  — compose merge simply makes the override authoritative for its own keys in its own services.
  Reserved-name skipping of operator and repository variables stays exactly as ADR-0008 defined it,
  and teardown is untouched.
- **Only the reserved trio travels this path.** Operator-defined stack variables keep flowing through
  the env file, and no new configuration knob is introduced: the per-service label is the entire
  control surface.

## Consequences

- **This is a behavior change for repositories that deliberately omitted passthrough.** Not every
  missing passthrough line is a mistake. A compose file that passed the token to `web` and pointedly
  not to a sidecar was expressing an intent, and a single-service stack that never mentioned the
  token at all was arguably expressing one too. After this change the default target service —
  a tenant's target service, or a plain stack's only service — receives the token whether or not the
  compose file ever named it. **`watchtower.inject-token: "false"` is the opt-out**, it is per
  service, and it is evaluated ahead of every default. Operators who want none of it label the
  services `"false"`; there is deliberately no global switch, because a host-wide flag would silently
  disable the feature for every stack that *does* depend on it.
- **The exposure bound is on Watchtower's *defaults*, not on the stack.** What the rules guarantee is
  that Watchtower never places the token in more than one service on its own initiative, and places
  it nowhere at all in a multi-service plain stack. That is the whole reason all-services injection
  was rejected: `WATCHTOWER_STACK_ID` and `WATCHTOWER_URL` are not secrets and can safely go
  everywhere, the token is and cannot.
- **Beyond the defaults, the repository decides — and that is in the threat model.** A compose file
  may label any number of services `watchtower.inject-token: "true"`, and more fundamentally it
  authors the service names and the labels the policy reads, so a repository can direct the token
  into whatever container it wants. This is not a hole the label opened: the repository *is* the code
  the operator chose to deploy, it already received the token under classic passthrough, and it could
  always forward it onward from there. Watchtower's job is to not hand a credential to containers the
  author never asked to have it; deciding which of the author's own containers hold it was never
  Watchtower's decision to make. The cost of the change is that the token's placement is now a policy
  with rules to learn instead of a line the author wrote.
- **One case trades a silent `401` for a silent nothing:** the plain multi-service stack, which still
  needs a label or a passthrough line. That is deliberate — it is precisely the case where Watchtower
  has no defensible answer — and it is now visible, because the deploy output states which services
  received which variables and warns when a target could not be resolved.
- **A second secret-bearing artifact exists per deploy.** The override file sits beside the generated
  `.env` — both are temp files alongside the clone directory, not inside it — and gets the same
  handling: `0600` on Linux, and its own explicit deletion when the deploy ends. Two files to keep
  out of logs and off disk instead of one, and two cleanups to get right on every exit path instead
  of one.
- **`docker compose config` moves from validation to load-bearing.** It already ran on the deploy
  path, but only its exit code mattered; its output is now parsed, so Watchtower takes a dependency
  on the shape of `--format json` and on the service labels the repository declares. The failure
  behaviour is unchanged — an unrenderable compose file fails the deploy with `config`'s own output,
  as it always did.
- **The split honours [ADR-0010](0010-target-kubesolo-runtime.md)'s dual-engine contract rule.** The
  *policy* is engine-neutral and names no compose concept — it is a map from service name to
  variables — while the override rendering, the `config --format json` parsing and the extra `-f` are
  Docker-private. The future Kube engine consumes the same plan and applies it natively as container
  environment entries in the pod spec; it needs no override-file analogue and inherits none of the
  env-file interpolation semantics, which is what a seam contract is supposed to allow.
- **Template authors get the zero-config path the App API always implied.** A tenant of a template
  reaches `/api/app/*` and renders a tenant switcher with no compose changes at all, which is what
  made ADR-0011 practical to ship to third-party template authors.

### Rejected alternatives

- **Inject the token into every service.** No policy, no label, no rules to document — and it hands
  the App API bearer to every database, cron sidecar and log shipper in the stack. On a management
  stack that credential can create and delete tenants, so the blast radius of one compromised
  auxiliary container would be the whole template. Scoping it is the entire point.
- **Keep requiring passthrough forever.** The status quo, and defensible on paper: the compose file
  states exactly what the container sees. In practice it produces silent `401`s at runtime for a
  mistake made at authoring time, it cannot be validated at deploy time, and it pushes the failure
  onto template *consumers* who did not write the compose file. Documenting a footgun is not fixing
  it.
- **Inject with `docker run -e`, or mutate containers after `up`.** Compose owns the container
  lifecycle; anything Watchtower sets outside it is lost the moment compose recreates a container,
  and the reconciliation fight would be invisible until it bit. It also has no counterpart in the
  Kube engine, where the answer is plainly "put it in the pod spec".
- **Rewrite the repository's compose file in place.** Achieves the same result with one file instead
  of two, and makes Watchtower an editor of user-authored content — responsible for preserving
  comments, anchors, formatting and merge semantics it has no business owning. The second `-f` is
  compose's own supported mechanism for adding configuration to a file you do not own.
