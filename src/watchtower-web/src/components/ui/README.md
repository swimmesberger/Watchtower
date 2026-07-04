# Watchtower UI kit — API reference

The design-system primitives for the redesign. **Colors come only from semantic tokens**
(`bg-surface`, `text-text-2`, `border-border`, `text-ok`, `bg-warn-bg`, …) defined in
`src/styles.css`. Never use raw hex in a component. One breakpoint governs all responsive
behavior: **`md` (768px)**.

React 19: `ref` is a normal prop — no `forwardRef` needed anywhere.

---

## Format helpers — `@/lib/format`

```ts
timeAgo(iso: string): string                      // "12s ago" · "4m ago" · "3h ago" · "2d ago"
absoluteTitle(iso?: string | null): string | undefined  // locale string for title="" (A9)
formatUptime(startedAt: string): string           // "3d 4h" · "5h 12m" · "8m" · "42s"
shortDigest(digest?: string | null): string       // "sha256:abc123def456…" · "—" when null
formatDuration(start: string, end?: string | null): string  // "12s" · "1m 30s"; end null ⇒ now
useElapsed(startedAt: string): string             // live "2m 14s", re-renders every 1s
```
Every relative timestamp should get `title={absoluteTitle(iso)}` and the `tnum` class.

## Theme — `@/lib/theme`

```ts
getTheme(): 'light' | 'dark' | 'system'
setTheme(t): void            // persists 'wt-theme', toggles .dark on <html>
toggleTheme(): void          // resolves system, then flips light/dark
resolveTheme(t?): 'light'|'dark'
useTheme(): { theme, resolved, setTheme, toggle }   // subscribes; `resolved` is 'light'|'dark'
```
The `.dark` class is applied pre-paint by an inline script in `index.html` (no FOUC).

## `useMediaQuery` — `@/hooks/use-media-query`

```ts
useMediaQuery(query: string): boolean   // SSR-safe
const isDesktop = useMediaQuery('(min-width: 768px)')
```

---

## Button — `@/components/ui/button`

```tsx
<Button variant="primary|secondary|ghost|danger|link"
        size="sm|md|default|icon|icon-sm"
        loading?={boolean} asChild?={boolean} … />
```
`loading` swaps the leading icon for a spinner and disables the button. `asChild` renders a
Radix `Slot` (use for links styled as buttons — note: `loading` is ignored with `asChild`).
Icons are auto-sized to 16px. Also exports `buttonVariants` for styling non-button elements.

```tsx
<Button variant="primary"><Play/> Deploy</Button>
<Button asChild variant="secondary"><Link to="/stacks/new">New stack</Link></Button>
```

## Card — `@/components/ui/card`

```tsx
<Card interactive?>
  <CardHeader><CardTitle/><CardDescription/></CardHeader>
  <CardContent/> <CardFooter/>
</Card>
```
`interactive` adds hover border-strong + subtle shadow. Padding 20px desktop / 16px mobile.

## StatCard — `@/components/ui/stat-card`

```tsx
<StatCard label="Failed" value={1}
          accent?="brand|ok|warn|danger|run|neutral"
          dotTone?="ok|warn|danger|run|queue|neutral"
          icon?={LucideIcon}
          to?={LinkProps['to']} search?={LinkProps['search']} />
```
Pass `to` (and optional `search`) to make the **whole card a router link** (A5) — it gains a
hover affordance and a chevron. Example (Dashboard → filtered Stacks):

```tsx
<StatCard label="Healthy" value={10} accent="ok" dotTone="ok" to="/stacks" search={{ status: 'ok' }} />
<StatCard label="Containers" value={38} />           {/* no `to` ⇒ not a link */}
```

## Badge — `@/components/ui/badge`

```tsx
<Badge tone="neutral|brand|ok|warn|run|queue|danger" size?="sm|md">3 updates</Badge>
```
Exports `BadgeTone` type and `badgeVariants`.

## StatusBadge — `@/components/ui/status-badge`

```tsx
<StatusBadge status={string | null} label?={string} size?="sm|md" pulse?={boolean} />
export function describeStatus(status): { tone, label, pulse }   // the raw mapping
```
Maps EVERY status vocabulary → tone + label + dot:
- stack `lastDeployStatus`: success/failed/running/queued/null
- deploy event status: queued/running/success/failed
- container `state`: running/exited/created/paused/restarting/dead/…
- container `health`: healthy/unhealthy/starting/null

The leading dot pulses (`wt-live`, motion-safe) only for live states (running/queued/starting/
restarting). Override with `pulse`. Includes visually-hidden "Status:" text.

```tsx
<StatusBadge status={stack.lastDeployStatus} />
<StatusBadge status={container.health ?? container.state} />
```

## Input / Textarea — `@/components/ui/input`

```tsx
<Input size?="sm|md" invalid?={boolean} mono?={boolean} … />
<Textarea invalid?={boolean} mono?={boolean} … />
```
`--surface-2` fill, `--border-strong`, brand focus ring. `mono` for tokens/paths. `invalid`
sets the danger border and `aria-invalid`. (`size` is the variant, not the HTML attribute.)

## Select — `@/components/ui/select` (Radix)

```tsx
<Select value onValueChange>
  <SelectTrigger><SelectValue placeholder="None" /></SelectTrigger>
  <SelectContent>
    <SelectItem value="1">ghcr (alice)</SelectItem>
  </SelectContent>
</Select>
```
Also: `SelectGroup`, `SelectLabel`, `SelectSeparator`. Checkmark on the selected item.
Note: Radix Select has no empty-string value — use a sentinel like `"none"` for "None".

## Switch — `@/components/ui/switch` (Radix)

```tsx
<Switch checked={on} onCheckedChange={setOn} />   // on = brand
```

## Label — `@/components/ui/label` (Radix)

```tsx
<Label htmlFor={id} required?={boolean} hint?={string}>Branch</Label>
```
Uppercase xs `--text-2`. Prefer `Field` (below) which wires this up for you.

## Field — `@/components/ui/field`

```tsx
<Field label="Compose file path" required? hint?="…" error?="…">
  {({ id, describedBy }) => (
    <Input id={id} aria-describedby={describedBy} mono value={…} onChange={…} />
  )}
</Field>
```
Wraps Label + control + hint/error with consistent rhythm. `children` may be a render-prop
(receives `{ id, describedBy }`) or plain nodes. `error` replaces `hint` and sets role="alert".
Use the guidance hints from A8.

## Dialog — `@/components/ui/dialog` (Radix)

```tsx
<Dialog open onOpenChange>
  <DialogTrigger asChild><Button>…</Button></DialogTrigger>
  <DialogContent hideClose?>
    <DialogHeader><DialogTitle/><DialogDescription/></DialogHeader>
    …
    <DialogFooter><DialogClose asChild><Button variant="secondary">Cancel</Button></DialogClose></DialogFooter>
  </DialogContent>
</Dialog>
```
Centered modal on desktop; bottom-sheet <640px (`sm:`). Wraps its own Portal + Overlay.
Also exports `DialogPortal`, `DialogOverlay`. Use for the mobile "add" forms (registries/credentials).

## ConfirmDialog — `@/components/ui/confirm-dialog` (Radix AlertDialog)

```tsx
<ConfirmDialog
  open? onOpenChange?          // controlled …
  trigger?={<Button variant="danger">Delete</Button>}   // … or trigger-based
  title="Delete web-app?"
  description={<>This permanently deletes …</>}
  confirmLabel="Delete" cancelLabel?="Cancel"
  tone?="danger|brand"
  loading?={mutation.isPending}
  requireText?="web-app"        // A4: confirm disabled until this is typed (mono input)
  onConfirm={() => mutation.mutate()} />
```
Replaces every `confirm()`. Focus starts on Cancel; Esc/scrim cancels. **Keep the dialog open
while `loading`; close it in the mutation's `onSettled` and fire a toast.** The confirm button
does NOT auto-close (so it can show the spinner). For stack deletion pass `requireText={stack.name}`.

## DropdownMenu — `@/components/ui/dropdown-menu` (Radix)

```tsx
<DropdownMenu>
  <DropdownMenuTrigger asChild><Button size="icon-sm" variant="ghost"><MoreHorizontal/></Button></DropdownMenuTrigger>
  <DropdownMenuContent>
    <DropdownMenuItem onSelect={…}>Test login</DropdownMenuItem>
    <DropdownMenuSeparator/>
    <DropdownMenuItem destructive onSelect={…}>Delete</DropdownMenuItem>
  </DropdownMenuContent>
</DropdownMenu>
```
Also: `DropdownMenuGroup`, `DropdownMenuLabel`. `destructive` tints an item danger. Used for
row overflow actions on mobile cards.

## Toast — `@/components/ui/toast` + `@/components/ui/use-toast`

`<Toaster/>` is already mounted in the root layout — do NOT mount another. Call from anywhere:

```ts
import { toast } from '@/components/ui/use-toast'
toast.success('Deleted web-app.')
toast.error('Login failed: 401 unauthorized.')
toast.info('Deploying web-app…')
toast({ tone: 'error', title: 'Deploy failed', description: msg,
        action: { label: 'Retry', onClick: () => m.mutate() } })   // full form
```
Tones success/error/info; auto-dismiss 4.5s (error 7s), max 3 (oldest dropped), swipe/× to close.
Viewport: bottom-right desktop, top-center mobile (below the 56px top bar). `dismissToast(id)` too.

## Skeleton — `@/components/ui/skeleton`

```tsx
<Skeleton variant?="rect|line|circle" className="h-9 w-full" />
```
Shimmer; freezes static under reduced-motion. (DataList has a built-in skeleton mode — below.)

## EmptyState — `@/components/ui/empty-state`

```tsx
<EmptyState icon={Boxes} title="No stacks yet"
  description="Register a git repo with a compose file to start deploying."
  action={<Button asChild><Link to="/stacks/new"><Plus/> New stack</Link></Button>}
  secondaryAction? />
```

## Tabs — `@/components/ui/tabs` (Radix)

```tsx
<Tabs defaultValue="overview">
  <TabsList><TabsTrigger value="overview">Overview</TabsTrigger><TabsTrigger value="settings">Settings</TabsTrigger></TabsList>
  <TabsContent value="overview">…</TabsContent>
</Tabs>
```
Underline style; active = brand 2px underline. Scrolls horizontally on mobile.

## DataList — `@/components/ui/data-list`

```tsx
<DataList
  items={stacks}
  getKey={(s) => s.id}
  columns={[
    { key: 'name', header: 'Name', cell: (s) => <Link…/> },
    { key: 'status', header: 'Status', cell: (s) => <StatusBadge status={s.lastDeployStatus}/> },
    { key: 'actions', header: '', align: 'right', cell: (s) => <RowActions s={s}/> },
  ]}
  renderCard={(s) => <StackCard stack={s} />}     // mobile <768px layout
  skeletonRows?={5}                                // loading: 5 table rows / 3 cards
  emptyState?={<EmptyState … />}
  onRowClick?={(s) => nav(s)}
  aria-label="Stacks" />
```
THE responsive table→card primitive (A11). Semantic `<table>` ≥768px (sticky zebra header,
44px rows); stacked cards <768px via `renderCard`. `DataListColumn<T>` type is exported.
`emptyState` renders only when not loading and items is empty.

## CopyButton — `@/components/ui/copy-button`

```tsx
<CopyButton value={url} label?="Copy" withToast?={true} variant?="ghost" size? />
```
Clipboard → check for 1.5s + "Copied to clipboard" toast (unless `withToast={false}`).

## SecretField — `@/components/ui/secret-field`

```tsx
<SecretField value={token} onChange? readOnly? copyable?={true}
             placeholder? aria-label? />
```
Masked mono value + eye toggle (+ copy). `readOnly` = reveal/copy a stored secret (webhook
tokens). Editable (default) = password input for entering env values / tokens.

## SectionHeader — `@/components/ui/section-header`

```tsx
<SectionHeader eyebrow? title="Repository" description? action={<Button/>} />
```
h2 + hairline underline + mb-4, right-aligned action slot.

## Banner — `@/components/ui/banner`

```tsx
<Banner tone="info|warn|ok|danger" title? icon?={LucideIcon}
        action?={<Button variant="link">Review</Button>}
        dismissible? onDismiss?>
  Watchtower update available.
</Banner>
```
`aria-live="polite"`. Uses `{tone}-bg/-bd` tokens. For self-update warnings, docker-config,
deploy status, and **query errors with a Retry action** (per spec §5).

## Spinner — `@/components/ui/spinner`

```tsx
<Spinner size?="sm|md|lg" label?="Loading" />   // currentColor ring
```

## Tooltip — `@/components/ui/tooltip` (Radix)

`TooltipProvider` is mounted in the root layout. For an icon-only action:

```tsx
<Tooltip label="Restart" side?="top"><Button size="icon-sm" variant="ghost"><RotateCw/></Button></Tooltip>
```
Low-level parts also exported: `TooltipRoot`, `TooltipTrigger`, `TooltipContent`, `TooltipProvider`.

## LiveLog — `@/components/ui/live-log`

```tsx
<LiveLog url={`${apiBase}/api/containers/${id}/logs?tail=100&follow=true`}
         active={open} maxHeight?="20rem" label?="log" />

// deploy-history stream ends with a named 'done' event:
<LiveLog url={`${apiBase}/api/stacks/events/${event.id}/stream`} active={expanded} doneEvent="done" />
```
THE shared SSE viewer (A3). Owns its own EventSource. Autoscrolls only while pinned to the
bottom; shows a "Jump to latest ↓" pill otherwise. Header shows a "● live" chip (`wt-live`)
while streaming, "reconnecting…" on error. aria-live is throttled (start + final status only).
Dark terminal inset in both themes. Pass `doneEvent` for streams that end with a named event
(the deploy stream); omit it for plain `onmessage` streams (container logs).

---

## EnvVarEditor — `@/components/env-var-editor`

```tsx
const [rows, setRows] = useState<StackEnvVarInput[]>([{ key: '', value: '' }])
<EnvVarEditor value={rows} onChange={setRows} />
// persist: rows.filter(v => v.key.trim() !== '')
```
Controlled. `value` is the DRAFT rows **including the trailing blank row** (start with one blank
row). A blank trailing row auto-appends so there's always an empty row; removing a row preserves
that invariant. Per-row show/hide on the value. Vertical mini-cards on mobile. Shared by New
Stack and Stack settings.

---

### Conventions the route rewrite must follow
- Semantic tokens only. Tailwind arbitrary values are for **dimensions**, never colors.
- Relative times: `<span className="tnum" title={absoluteTitle(iso)}>{timeAgo(iso)}</span>` (A9).
- Every mutation gets `onSuccess`/`onError` toasts; every `confirm()` → `ConfirmDialog`.
- Query errors → in-panel `Banner` (danger) with Retry; mutation errors → toast.
- Polling backoff per A7 (dashboard 2.5s active / 10s idle; containers 10s/30s; the `● live`
  chip renders only while the fast interval is active).
- All 29 RPC methods keep their current signatures — `src/lib/api.ts` is frozen.
```
