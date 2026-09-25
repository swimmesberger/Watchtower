# ADR-0041: Operators get Web Push notifications, starting with failed deploys

- Status: Accepted
- Date: 2026-09-25
- Related: [ADR-0024](0024-postgresql-only-and-state-in-the-database.md) (why the VAPID key pair is a row, encrypted like
  every other private key), [docs/central-auth/design.md](../central-auth/design.md) §13 (the system
  realm — who counts as an operator), Elarion Web Push support ([swimmesberger/Elarion#162](https://github.com/swimmesberger/Elarion/issues/162) — the replacement for the
  interim seams below).

## Context

A deploy that fails is only visible to someone looking at Watchtower. That is fine for a deploy an
operator just clicked, and useless for everything Watchtower starts on its own: the daily auto-deploy
window, pull-based updates, a release fanning out to fifty tenants overnight. The deploy history records
the failure faithfully; nobody reads it until something downstream is already broken.

Watchtower's UI is already an installable PWA, and every current browser — including iOS Safari from 16.4
for a web app added to the home screen — implements the Web Push standard (RFC 8030 delivery, RFC 8291
payload encryption, RFC 8292 VAPID). That reaches an operator's phone without an app-store app, a
third-party notification account or an email relay, and without the page being open.

## Decision

1. **Web Push via VAPID, for operators only.** A browser subscribes through `notifications.subscribe`
   (the handlers are operator-only, like every management handler, via the system-realm rule). A
   notification goes to every subscription whose owner is an operator *at send time*: an enabled account
   of the system realm, or the implicit `local` operator while authentication is switched off. Evaluating
   the audience per send means disabling an account or moving it out of the system realm silences its
   devices without anyone finding them. A subscription belongs to whoever enabled notifications on that
   browser last — the endpoint is unique, and a re-subscribe reassigns it rather than duplicating it.

2. **The first event is "deploy failed".** The deploy engine reports every deploy that ends `failed`
   through a fire-and-forget port (`IDeployFailureSink`) at its single completion point; the
   Notifications module drains a bounded in-memory queue (drop-oldest) off the deploy path. Only the
   deploy event id crosses; the notifier loads the stack and trigger itself and skips a stack that has
   been deleted since.

3. **The payload carries no deploy output.** Title (`Deploy failed: <stack>`), a body naming the trigger,
   a link to the event's log (`/stacks/<id>?event=<eventId>`) and a per-stack collapse tag — nothing else.
   Deploy output can contain secrets, and a push payload is handed to Google, Apple or Mozilla (encrypted,
   but still leaving the machine). The log stays behind Watchtower's own authentication.

4. **Automatic triggers notify on the transition only.** A deploy someone explicitly asked for — manual,
   webhook, release-manual, volume-recreate — always notifies. An automatic one (auto-update, schedule,
   release, release-reconcile: `DeployTriggers.IsAutomatic`) notifies only when the stack's previous
   finished deploy did not also fail. Automatic triggers repeat; without the guard, a stack broken for a
   reason nobody has fixed yet would page on every daily window and every reconcile tick.

5. **Keys need no setup.** `Watchtower:WebPush` may pin a subject and a key pair; otherwise a P-256 pair
   is generated on first use and stored as a single row (`vapid_key_pairs`, id 1 — the fixed id is the
   first-use race guard across instances), its private half encrypted through `KeyProtector`. Changing
   the pair orphans every existing subscription, which is why a generated pair is kept rather than
   regenerated.

6. **Interim implementation.** Elarion is expected to ship Web Push support (swimmesberger/Elarion#162). Until it does, the generic
   parts live in the Notifications module behind seams shaped like that future package — VAPID key
   management, subscription storage, the send loop (with 404/410 and unusable-key cleanup) and the
   transport over `Lib.Net.Http.WebPush` — each marked `TODO(elarion#162)` and registered by one
   method, so the swap replaces a registration rather than rewriting the Watchtower-specific audience and
   deploy-failure logic on top.

## Consequences

- An operator is told about a failed deploy on their phone within seconds, whether or not Watchtower is
  open — including the overnight, automatic ones that nobody was watching.
- iOS delivers only to a web app **added to the home screen** (iOS/iPadOS 16.4+); Safari tabs cannot
  subscribe. The UI has to say so rather than offer a toggle that silently does nothing.
- Delivery is best-effort: the queue is in memory (a failure during a restart is not notified — the deploy
  history still records it), a push service may drop an undelivered message after its 12-hour TTL, and a
  device with notifications blocked accepts nothing. The deploy history remains the record.
- Two tables and one dependency (`Lib.Net.Http.WebPush`) exist until the Elarion package replaces them.
- Deploy failures written outside the deploy engine's completion point — the pre-deploy-backup refusal
  in `BackupChainCoordinator` and the startup sweep that fails deploys interrupted by a restart — do not
  notify. The first is a candidate for the same port; the second deliberately is not (a restart would
  page for every deploy it interrupted).
