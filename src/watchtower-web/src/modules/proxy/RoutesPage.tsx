import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronDown, ChevronUp, CloudDownload, Download, ExternalLink, Globe, Pencil, Plus, RefreshCw, ShieldCheck, Trash2, X } from 'lucide-react'
import { api, INTERNAL_CA_DOWNLOAD_URL } from '@/lib/api'
import type {
  AccessMode,
  AccessRule,
  ActiveEnforcementPoint,
  CertificateInfo,
  CloudflareForeignRoute,
  CreateRouteRequest,
  DomainKind,
  IdentityHeaderMode,
  Route,
  RouteAccess,
  RouteAccessModeWire,
  RouteBinding,
  RouteStatus,
  RouteTarget,
  UpdateRouteRequest,
} from '@/lib/types'
import { LOCAL_USER_ID } from '@/lib/auth'
import { absoluteTitle, timeAgo } from '@/lib/format'
import { parseLanNames } from '@/lib/lanNames'
import { bestPrimaryDomain, composeHost, splitHost, subdomainOf } from '@/lib/primaryDomains'
import { useRealms } from '@/hooks/use-realms'
import { Badge, type BadgeTone } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, type InputProps, Textarea } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { SectionHeader } from '@/components/ui/section-header'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/use-toast'
import { AccessRulesCard, isAttachableAt } from './AccessRulesCard'
import { routesRoute } from './module'

const STATUS_TONE: Record<RouteStatus, BadgeTone> = {
  active: 'ok',
  error: 'danger',
  awaitingdns: 'warn',
  pending: 'neutral',
}

const STATUS_LABEL: Record<RouteStatus, string> = {
  active: 'Active',
  error: 'Error',
  awaitingdns: 'Awaiting DNS',
  pending: 'Pending',
}

/** The three access modes in menu order, with the copy the access editor shows for each. */
const ACCESS_MODES: { value: AccessMode; label: string; description: string }[] = [
  { value: 'Public', label: 'Public', description: 'No access control — every request is proxied.' },
  {
    value: 'Authenticated',
    label: 'Any authenticated user',
    description: 'Any signed-in Watchtower user may enter; anonymous requests go to the login page.',
  },
  {
    value: 'Restricted',
    label: 'Selected users and groups',
    description: 'Only the users and group members you pick below may enter.',
  },
]

/**
 * The Access badge, keyed on the lowercase mode `proxy.listRoutes` reports. `Public` is the warning one:
 * since ADR-0035 a route is protected unless somebody chose otherwise, so an unguarded address is the
 * exception worth spotting in the list, not the norm.
 */
const ACCESS_TONE: Record<RouteAccessModeWire, BadgeTone> = {
  public: 'warn',
  authenticated: 'ok',
  restricted: 'ok',
}

const ACCESS_LABEL: Record<RouteAccessModeWire, string> = {
  public: 'Public',
  authenticated: 'Protected',
  restricted: 'Restricted',
}

/**
 * What the tooltip beside the badge says. Public gets a sentence of its own: the dialog's copy describes
 * the choice ("no access control — every request is proxied") where a row has to answer "who gets in?".
 */
const ACCESS_DESCRIPTION: Record<RouteAccessModeWire, string> = {
  public: 'Anyone can reach this route.',
  authenticated: accessModeDescription('Authenticated'),
  restricted: accessModeDescription('Restricted'),
}

function accessModeDescription(value: AccessMode): string {
  return ACCESS_MODES.find((m) => m.value === value)?.description ?? ''
}

/**
 * What a mode means **at the enforcement point that is actually live**. `Authenticated` is the one that
 * needed this: under the built-in proxy it really is "any signed-in Watchtower user", but under Cloudflare
 * Watchtower's forward-auth does not run at all and the mode means "whoever passes the attached access
 * rules, or the instance-wide allow sources when none are attached" (ADR-0039). The static description
 * described a mechanism that is not running there, which is exactly the kind of mislabelling the whole
 * access plane is supposed to avoid.
 */
function accessModeDescriptionAt(value: AccessMode, point: ActiveEnforcementPoint): string {
  if (point !== 'CloudflareAccess') return accessModeDescription(value)
  switch (value) {
    case 'Authenticated':
      return 'Cloudflare Access decides. Tick the access rules that admit people here, or leave them unticked to use the instance-wide allow sources from Settings.'
    case 'Restricted':
      return 'Only the users and group members you pick below may enter — matched by email address at the Cloudflare edge.'
    default:
      return accessModeDescription(value)
  }
}

/** The identity-forwarding modes in menu order, with the label the access editor shows for each. */
const IDENTITY_HEADER_MODES: { value: IdentityHeaderMode; label: string }[] = [
  { value: 'None', label: 'JWT only (default)' },
  { value: 'Remote', label: 'Remote-* headers (Authelia/Traefik)' },
  { value: 'AuthRequest', label: 'X-Auth-Request-* headers (oauth2-proxy)' },
  { value: 'Cloudflare', label: 'Cf-Access-* headers (Cloudflare Access)' },
]

const emptyForm = {
  // How the route is addressed (ADR-0033). `domain` is the default because it is what nearly every route
  // is; `port` drops the hostname half of the form entirely.
  binding: 'domain' as RouteBinding,
  // What the hostname is served by (ADR-0023). `service` is the default because it is what nearly every
  // route is; `watchtower` swaps the stack/service/port half of the form for a realm picker.
  target: 'service' as RouteTarget,
  realmId: '',
  makeLoginRoute: true,
  stackId: '',
  // `domain` is still the hostname on the wire, and the only field the create request carries. With
  // primary domains configured it is composed instead of typed: `subdomain` + `primaryDomain` spell the
  // host, and an empty subdomain means the apex — a route on the primary domain itself. `customHostname`
  // is the escape hatch back to typing `domain` in full, for a hostname no primary domain covers.
  domain: '',
  subdomain: '',
  primaryDomain: '',
  customHostname: false,
  serviceName: '',
  containerPort: '',
  listenPort: '',
  tlsEnabled: true,
  // The access policy to create the route under (ADR-0035). Empty means "untouched": the request carries
  // no mode at all and the configured default decides, which is also the only shape a non-administrator
  // may send. Naming one explicitly is admin-only, so an empty field is never a silent downgrade.
  accessMode: '' as AccessMode | '',
  bypassPaths: '',
  // The rest of the policy, so a route is created with everything an edit could give it
  // afterwards (ADR-0039, decision 5 as amended) — never published under one policy and then changed.
  identityHeaderMode: 'None' as IdentityHeaderMode,
  grantedUserIds: [] as number[],
  grantedGroupIds: [] as number[],
  accessRuleIds: [] as number[],
  // True once the user opts out of the discovered-value dropdown to type a custom value.
  serviceManual: false,
  portManual: false,
}

/** The two route targets in menu order, with the copy the create form shows for each. */
const ROUTE_TARGETS: { value: RouteTarget; label: string; description: string }[] = [
  {
    value: 'service',
    label: 'Stack service',
    description: 'Forward the domain to a container inside one of your stacks.',
  },
  {
    value: 'watchtower',
    label: 'Watchtower (this instance)',
    description: "Serve Watchtower's own UI and API on the domain — and, for a realm's login route, its login page.",
  },
]

/** The two ways to address a route, in menu order, with the copy the create form shows for each. */
const ROUTE_BINDINGS: { value: RouteBinding; label: string; description: string }[] = [
  {
    value: 'domain',
    label: 'Domain (public, automatic certificates)',
    description:
      'A hostname on the shared ingress listeners, with a certificate issued automatically over ACME.',
  },
  {
    value: 'port',
    label: 'Port (LAN only, internal CA)',
    description:
      "A TLS port of its own on this host, with no hostname — for a service reached by address on the "
      + "local network. The certificate comes from Watchtower's own CA, which you import once.",
  },
]

const MANUAL = '__manual__'

/**
 * A LAN name as it goes into a URL authority. An IPv6 literal has to be bracketed — `fd00::10:9001` is
 * not an authority a browser can parse, `[fd00::10]:9001` is — and a zone suffix (`fe80::1%3`) names an
 * interface on the host that typed it, which no URL carries. Hostnames and IPv4 pass through untouched,
 * and a name the operator already bracketed is not bracketed twice.
 */
function authorityHost(name: string): string {
  if (!name.includes(':')) return name
  return `[${name.replace(/^\[|\]$/g, '').replace(/%.*$/, '')}]`
}

/**
 * How a route is named in a message, a tooltip or an aria-label: its hostname where there is one, and
 * the port otherwise — a port route has no name of its own (ADR-0033). Mirrors `Route.DisplayAddress`
 * on the server, so the two never call the same row different things.
 */
function routeLabel(r: Route): string {
  return r.domain ?? (r.listenPort != null ? `port ${r.listenPort}` : `route ${r.id}`)
}

/**
 * The URL a visitor types for a route. A port route has no hostname of its own, so the address is built
 * from the deployment's first LAN name and the listen port — the other names reach it just as well, and
 * the row's tooltip lists them.
 */
function routeUrl(r: Route, lanNames: string[], https: boolean): string | null {
  if (r.binding === 'port') {
    const first = lanNames[0]
    return first ? `https://${authorityHost(first)}:${r.listenPort}` : null
  }
  return r.domain ? `${https ? 'https' : 'http'}://${r.domain}` : null
}

/**
 * Why a Watchtower route has no access policy, said in one place so the edit form and the
 * create form cannot describe the same rule differently.
 */
const WATCHTOWER_ACCESS_NOTE =
  "Watchtower authenticates visitors with its own login — route access control does not apply."

/** The same, for a port route: there is no hostname a login redirect could bring the visitor back to. */
const PORT_ACCESS_NOTE =
  'A port route is always public — it has no hostname for a login redirect to return to.'

/** Why a route has no access policy to edit, or null when it has one. */
function accessNote(r: Route): string | null {
  if (r.target === 'watchtower') return WATCHTOWER_ACCESS_NOTE
  if (r.binding === 'port') return PORT_ACCESS_NOTE
  return null
}

/** localStorage key for the "Found in Cloudflare" card's collapsed state. */
const FOREIGN_COLLAPSED_KEY = 'watchtower:routes:foreign-collapsed'

/**
 * A select populated from discovered values with a manual-entry escape hatch. Renders a plain text
 * input when there's nothing to choose from (no live containers) or the user opts to type a custom
 * value, and a disabled placeholder while the options are still loading.
 */
function ComboField({
  id,
  describedBy,
  value,
  onChange,
  options,
  manual,
  onManualChange,
  loading,
  placeholder,
  inputProps,
}: {
  id?: string
  describedBy?: string
  value: string
  onChange: (value: string) => void
  options: string[]
  manual: boolean
  onManualChange: (manual: boolean) => void
  loading?: boolean
  placeholder: string
  inputProps?: InputProps
}) {
  if (loading) {
    return <Input {...inputProps} id={id} aria-describedby={describedBy} disabled placeholder="Loading…" />
  }

  if (manual || options.length === 0) {
    return (
      <div className="flex flex-col gap-1.5">
        <Input
          {...inputProps}
          id={id}
          aria-describedby={describedBy}
          value={value}
          onChange={(e) => onChange(e.target.value)}
        />
        {options.length > 0 && (
          <button
            type="button"
            className="self-start text-xs text-text-3 transition-colors hover:text-text-2"
            onClick={() => {
              onManualChange(false)
              onChange('')
            }}
          >
            Choose from list
          </button>
        )}
      </div>
    )
  }

  return (
    <Select
      value={value}
      onValueChange={(v) => {
        if (v === MANUAL) {
          onManualChange(true)
          onChange('')
        } else {
          onChange(v)
        }
      }}
    >
      <SelectTrigger id={id} aria-describedby={describedBy}>
        <SelectValue placeholder={placeholder} />
      </SelectTrigger>
      <SelectContent>
        {options.map((o) => (
          <SelectItem key={o} value={o}>
            {o}
          </SelectItem>
        ))}
        <SelectItem value={MANUAL}>Enter manually…</SelectItem>
      </SelectContent>
    </Select>
  )
}

export function RoutesPage() {
  const qc = useQueryClient()
  const { caps } = routesRoute.useRouteContext()
  const [showForm, setShowForm] = useState(false)
  const [form, setForm] = useState({ ...emptyForm })
  // The route the form is editing, or null while it is creating one. One form for both, on purpose: an
  // existing route can then be changed in exactly the ways a new one can be set up, and a field added to
  // the create half later is an edit field too without anybody having to remember that.
  const [editingRoute, setEditingRoute] = useState<Route | null>(null)
  const isEditing = editingRoute != null
  const [pendingDelete, setPendingDelete] = useState<Route | null>(null)

  const { data: status } = useQuery({ queryKey: ['proxy-status'], queryFn: api.proxy.getStatus })
  // Under the Cloudflare Tunnel provider TLS terminates at Cloudflare's edge: every route is served over
  // HTTPS and the per-route flag (a Caddy knob — auto-managed certificate vs plain HTTP) controls
  // nothing, so the form hides it and the list reports what is actually served.
  const isCloudflare = status?.provider === 'cloudflare'
  const servesHttps = (r: Route) => isCloudflare || r.tlsEnabled

  // Access policy is an admin operation, so the affordance is shown only to an administrator — and, under
  // the built-in and Caddy providers, only on an auth-enabled deployment, because there the policy is
  // meaningless without auth (the proxy only emits forward_auth when it is on). The implicit local
  // administrator (Auth:Enabled=false) reports the reserved `local` id — see auth.ts. Under Cloudflare the
  // gate is a Zero Trust Access application, which Watchtower's own auth has no part in, so the local
  // administrator gets the controls there: with routes protected by default (ADR-0035) they are how a
  // route is made Public at all.
  const canManageAccess = caps.hasRole('Admin') && (caps.user.id !== LOCAL_USER_ID || isCloudflare)

  // Port routes need only the proxy to be on (ADR-0033 addendum). Their listener is on Watchtower's
  // own container and their certificate comes from Watchtower's own CA, so which provider terminates
  // the public domains has nothing to say about them — they are offered alongside Caddy and the tunnel
  // exactly as they are under the built-in provider.
  const supportsPortRoutes = status?.enabled === true
  // The LAN names the internal CA issues for. Needed for two things at once: the addresses the table
  // renders for port routes, and whether creating one is possible at all.
  // The key is the Settings page's, deliberately: saving the proxy card invalidates ['proxy'], and a
  // key of this page's own would leave the "no LAN names" banner up — and the submit button disabled —
  // for a staleTime after the operator went and configured exactly what it asked them to.
  // Also fetched for the access half of the create form, which needs the default mode and the Cloudflare
  // allow sources — and needs them whether or not the proxy is on, because a route can be created ahead
  // of turning it on and would still be created protected.
  const proxyConfigQuery = useQuery({
    queryKey: ['proxy', 'config'],
    queryFn: api.proxy.getConfig,
    enabled: supportsPortRoutes || canManageAccess,
  })
  const proxyConfig = proxyConfigQuery.data
  const lanNames = useMemo(() => parseLanNames(proxyConfig?.portRoutes.lanNames), [proxyConfig])
  // The name that stands in for the rest wherever one address is shown: every configured name reaches a
  // port route, and the tooltip beside it lists the others.
  const firstLanName = lanNames[0]
  // "None configured" is a claim about an answer, not about the absence of one: while the query is in
  // flight or after it failed there are no names either, and telling the operator to go set them —
  // then refusing the submit — would be sending them after something that may already be there.
  const lanNamesKnown = proxyConfig != null
  const lanNamesUnavailable = proxyConfigQuery.isError

  // What a new route starts under when the form doesn't say (ADR-0035). Protected while the settings are
  // still loading: that is the shipped default, and showing "Public" first would misdescribe what the
  // Create button is about to do.
  const defaultAccessMode: AccessMode =
    proxyConfig?.defaultAccessMode === 'public' ? 'Public' : 'Authenticated'
  const formAccessMode = form.accessMode || defaultAccessMode
  // Under Cloudflare a protected route's Access application needs somebody to let in. With all four allow
  // sources blank the reconcile publishes deny-all, so the server refuses the create — say so before the
  // operator fills the form in. Only once the settings actually answered: a query in flight is not an
  // empty configuration.
  const cfAllowSourceMissing =
    isCloudflare &&
    proxyConfig != null &&
    proxyConfig.cloudflare.accessAllowedEmails.trim() === '' &&
    proxyConfig.cloudflare.accessAllowedEmailDomains.trim() === '' &&
    proxyConfig.cloudflare.accessGroupIds.trim() === '' &&
    proxyConfig.cloudflare.accessReusablePolicyIds.trim() === ''

  // The base domains routes live under (ADR-0036): the configured list merged with whatever zones the
  // Cloudflare token can see. Two jobs at once — the create form's domain dropdown, and how the list
  // below is grouped. Kept a long time and not refetched on focus: this answers what an operator
  // publishes under, which does not move while they fill in a form. Never errors server-side, so an
  // empty answer is simply "nothing configured" and the page falls back to the flat, type-it-in-full
  // shape it had before.
  const primaryDomainsQuery = useQuery({
    queryKey: ['proxy', 'primary-domains'],
    queryFn: api.proxy.listPrimaryDomains,
    staleTime: 5 * 60_000,
    refetchOnWindowFocus: false,
  })
  const primaryDomains = useMemo(() => primaryDomainsQuery.data ?? [], [primaryDomainsQuery.data])
  const primaryNames = useMemo(() => primaryDomains.map((d) => d.name), [primaryDomains])
  // The one an untouched dropdown stands on. Empty only where there are no primary domains at all,
  // which is exactly where the composed control is not rendered and `composed` never reads it.
  const firstPrimaryName = primaryNames[0] ?? ''

  // Public hostnames configured on the tunnel in the Cloudflare dashboard that the route table
  // doesn't know. The reconcile preserves them; this surfaces them for one-click adoption. Failures
  // and "the tunnel cannot be seen" both render as a banner — a silently empty list here reads as
  // "my Cloudflare routes are not showing up".
  const foreignQuery = useQuery({
    queryKey: ['cloudflare-foreign-routes'],
    queryFn: api.proxy.listCloudflareForeignRoutes,
    enabled: status?.enabled === true && status.provider === 'cloudflare',
    staleTime: 60_000,
  })
  const foreignRoutes = foreignQuery.data?.routes ?? []
  // Collapsed state persists across visits: once the operator has imported what they wanted, the
  // remaining dashboard hostnames are reference, not a to-do, and should not fill the screen each time.
  const [foreignCollapsed, setForeignCollapsed] = useState(
    () => localStorage.getItem(FOREIGN_COLLAPSED_KEY) === '1',
  )
  function toggleForeign() {
    setForeignCollapsed((collapsed) => {
      localStorage.setItem(FOREIGN_COLLAPSED_KEY, collapsed ? '0' : '1')
      return !collapsed
    })
  }
  const foreignWarning = foreignQuery.isError
    ? ((foreignQuery.error as Error)?.message ?? 'Could not read the tunnel configuration from Cloudflare.')
    : (foreignQuery.data?.warning ?? null)

  const {
    data: routes = [],
    isLoading,
    isError,
    error,
    refetch,
  } = useQuery({
    queryKey: ['routes'],
    queryFn: api.proxy.listRoutes,
    // Poll while any route is still provisioning (cert not yet issued).
    refetchInterval: (q) =>
      (q.state.data ?? []).some((r) => r.status === 'pending' || r.status === 'awaitingdns')
        ? 5000
        : false,
  })

  // Whether each port route's host port is actually published on Watchtower's container (ADR-0033). A
  // port route is served the moment it exists, but nothing outside the container can reach it until the
  // container publishes that port — and Docker can only do that by recreating it. Asked whenever the
  // in-process provider is active rather than only when a port route exists: a port left published by a
  // route that has since been deleted is precisely the case with no route to gate on.
  const portBindingsQuery = useQuery({
    queryKey: ['proxy', 'port-bindings'],
    queryFn: api.proxy.getPortBindings,
    enabled: supportsPortRoutes,
    staleTime: 15_000,
  })
  const portBindings = portBindingsQuery.data
  // Everything below is a claim about what the container publishes, and there is an answer to that only
  // when the container could be inspected. Where it could not, the server reports every port as unbound —
  // which is the absence of an answer, not the answer "no". Reading it as "no" would mark every port
  // route on a bare-metal install broken while it is serving perfectly, and would do the same to a
  // healthy deployment for as long as the Docker socket hiccups.
  const portsKnown = portBindings?.containerDetected === true
  const knownPorts = useMemo(
    () => (portsKnown ? (portBindings?.ports ?? []) : []),
    [portBindings, portsKnown],
  )
  const bindingByPort = useMemo(
    () => new Map(knownPorts.map((p) => [p.port, p])),
    [knownPorts],
  )
  const portRoutes = useMemo(() => routes.filter((r) => r.binding === 'port'), [routes])
  const pendingPorts = useMemo(() => knownPorts.filter((p) => !p.bound), [knownPorts])
  // Ports Watchtower published for a route that has since been deleted. No row of their own — the route
  // that named them is gone — so the banner is the only place they can be mentioned at all.
  const stalePorts = portsKnown ? (portBindings?.pendingUnpublish ?? []) : []
  const firstPending = pendingPorts[0]
  const hasPendingPorts = pendingPorts.length > 0 || stalePorts.length > 0
  // The same restart does both, so every piece of copy about it names whichever half is actually
  // pending — from the banner's button through to the confirmation it opens. "Publish" in front of a
  // recreate that is only going to give a leftover port back describes the opposite of what happens.
  const releaseOnly = pendingPorts.length === 0
  // Every port the button would publish is already held by another container, so the recreate it starts
  // could only fail to start and roll back. Offering it anyway would cost a restart to learn nothing.
  // A mix is left enabled: the publishable half is still worth a recreate.
  const allPendingBlocked = pendingPorts.length > 0 && pendingPorts.every((p) => p.blockedBy != null)
  // One reason, whichever applies — the button is disabled with exactly one explanation attached.
  const publishRefusal =
    portBindings?.unavailableReason ?? (allPendingBlocked ? (pendingPorts[0]?.blockedBy ?? null) : null)
  const publishActionLabel = releaseOnly
    ? 'Release ports & restart Watchtower (~5 s)'
    : 'Publish ports & restart Watchtower (~5 s)'
  // The other state worth a word, and a different one: there are port routes and whether their ports are
  // reachable is simply not knowable from here. Said as that, without a verdict on the routes.
  const portsUnknown = portBindings != null && !portsKnown && portRoutes.length > 0
  // Publishing restarts the management plane, so it is never automatic and never a single click.
  const [confirmPublish, setConfirmPublish] = useState(false)
  const publishPorts = useMutation({
    mutationFn: () => api.proxy.applyPortBindings(),
    onSuccess: (result) => {
      // The response arrives before the restart does — the coordinator waits three seconds for exactly
      // this. Saying "restarting" rather than "done" is the honest description of what happens next.
      if (result.restarting) toast.success(result.message)
      else toast.info(result.message)
      qc.invalidateQueries({ queryKey: ['proxy', 'port-bindings'] })
    },
    onError: (err: Error) => toast.error(err.message),
    onSettled: () => setConfirmPublish(false),
  })

  const { data: stacks = [] } = useQuery({ queryKey: ['stacks'], queryFn: api.stacks.list })

  // The populations a Watchtower route can serve. Admin-gated like realms.list itself, so a
  // non-administrator simply sees an empty roster and the Watchtower target defaults to the operator
  // realm the server would have chosen anyway.
  const { realms, systemRealmId } = useRealms({ enabled: caps.hasRole('Admin') })
  const isPortForm = form.binding === 'port'
  // A port route is always a stack service (ADR-0033), so the Watchtower half of the form is out of
  // reach in port mode whatever the target field happens to hold.
  const isWatchtowerForm = !isPortForm && form.target === 'watchtower'
  const formRealmId = form.realmId === '' ? systemRealmId : Number(form.realmId)
  const formRealm = realms.find((r) => r.id === formRealmId)

  // The hostname the form is actually about, whichever way it was entered — the one value the DNS check
  // and the create request both use. With no primary domains configured, or once the operator has opted
  // into a custom hostname, that is the field they typed; otherwise it is the two halves spelled
  // together, defaulting to the first primary domain so an untouched dropdown still names one.
  const composed =
    form.customHostname || primaryNames.length === 0
      ? form.domain.trim()
      : composeHost(form.subdomain, form.primaryDomain || firstPrimaryName)

  const selectedStack = stacks.find((s) => String(s.id) === form.stackId)
  const stackProject = selectedStack?.composeProjectName

  // The form's access editor — the one place a route's access is decided, for a new route and an existing
  // one alike. Only for an administrator, and only on a service route to a domain: a port route and a
  // Watchtower route are Public by definition.
  const wantsAccessEditor = showForm && canManageAccess && !isPortForm && !isWatchtowerForm
  // A new route has no realm until its stack is chosen; asked of the server, which resolves it exactly as
  // proxy.createRoute validates, so the pickers can only offer what the create will accept.
  const createStackId = !isEditing ? (selectedStack?.id ?? null) : null
  const { data: stackAccess } = useQuery({
    queryKey: ['stack-access-context', createStackId],
    queryFn: () => api.proxy.getStackAccessContext(createStackId!),
    enabled: wantsAccessEditor && !isEditing && createStackId != null,
  })
  // An existing route's stored policy, with its realm and the enforcement point that decides it.
  const { data: editAccess } = useQuery({
    queryKey: ['route-access', editingRoute?.id],
    queryFn: () => api.proxy.getAccess(editingRoute!.id),
    enabled: wantsAccessEditor && isEditing,
  })
  const accessRealmId = !wantsAccessEditor ? undefined : isEditing ? editAccess?.realmId : stackAccess?.realmId
  const { data: accessUsers = [] } = useQuery({
    queryKey: ['users', { realmId: accessRealmId }],
    queryFn: () => api.users.list(accessRealmId),
    enabled: accessRealmId != null,
  })
  const { data: accessGroups = [] } = useQuery({
    queryKey: ['groups', { realmId: accessRealmId }],
    queryFn: () => api.groups.list(accessRealmId),
    enabled: accessRealmId != null,
  })
  const { data: accessRules = [] } = useQuery({
    queryKey: ['access-rules', { realmId: accessRealmId }],
    queryFn: () => api.proxy.listAccessRules(accessRealmId),
    enabled: accessRealmId != null,
  })
  // Until the server has answered, the provider already on the page is the same answer it will give.
  const accessEnforcementPoint: ActiveEnforcementPoint =
    (isEditing ? editAccess?.activeEnforcementPoint : stackAccess?.activeEnforcementPoint) ??
    (isCloudflare ? 'CloudflareAccess' : 'InProcess')
  // An edit's changes to the stored policy; null until the operator touches it, so the editor shows the
  // stored policy as it arrives rather than a copy taken before it had loaded.
  const [editAccessDraft, setEditAccessDraft] = useState<AccessDraft | null>(null)
  const editDraft = editAccessDraft ?? (editAccess ? accessDraftFrom(editAccess) : null)

  // The selected stack's live containers, used to drive the service + port dropdowns.
  const { data: portsData, isFetching: portsFetching } = useQuery({
    queryKey: ['stack-ports', stackProject],
    queryFn: () => api.networks.ports(stackProject),
    enabled: !!stackProject,
  })
  const portsLoading = !!stackProject && portsFetching && !portsData

  // Compose service → its distinct container ports, from the stack's running containers.
  const portsByService = useMemo(() => {
    const map = new Map<string, Set<number>>()
    for (const p of portsData?.published ?? []) {
      if (!p.serviceName) continue
      let ports = map.get(p.serviceName)
      if (!ports) map.set(p.serviceName, (ports = new Set()))
      ports.add(p.privatePort)
    }
    return map
  }, [portsData])

  const serviceOptions = useMemo(() => [...portsByService.keys()].sort(), [portsByService])
  const portOptions = useMemo(() => {
    const ports = portsByService.get(form.serviceName)
    return ports ? [...ports].sort((a, b) => a - b).map(String) : []
  }, [portsByService, form.serviceName])

  const dns = useMutation({ mutationFn: (domain: string) => api.proxy.checkDns(domain) })

  const create = useMutation({
    mutationFn: (data: CreateRouteRequest) => api.proxy.createRoute(data),
    onSuccess: (route) => {
      toast.success(`Route ${routeLabel(route)} created.`)
      qc.invalidateQueries({ queryKey: ['routes'] })
      // A new port route mints the internal CA on first use, so the block that offers its root
      // appears. The Settings card reads it under this same key — creating the first port route is
      // exactly when its download link has to stop being hidden.
      qc.invalidateQueries({ queryKey: ['proxy', 'internal-ca'] })
      // A new port route is a new host port that is almost certainly not published yet — which is the
      // banner offering to publish it, so it has to be recomputed rather than waiting out a staleTime.
      qc.invalidateQueries({ queryKey: ['proxy', 'port-bindings'] })
      // An imported hostname stops being foreign the moment its route row exists.
      qc.invalidateQueries({ queryKey: ['cloudflare-foreign-routes'] })
      // A route created with access rules attached changes what the rules card counts.
      qc.invalidateQueries({ queryKey: ['access-rules'] })
      setForm({ ...emptyForm })
      dns.reset()
      setShowForm(false)
    },
    onError: (err: Error) => toast.error(err.message),
  })

  const update = useMutation({
    mutationFn: ({ id, data }: { id: number; data: UpdateRouteRequest }) => api.proxy.updateRoute(id, data),
    onSuccess: (route) => {
      toast.success(`Route ${routeLabel(route)} updated.`)
      qc.invalidateQueries({ queryKey: ['routes'] })
      // A moved listen port is a new host port to publish and an old one to release — both of which are
      // what the port banner is about.
      qc.invalidateQueries({ queryKey: ['proxy', 'port-bindings'] })
      // A renamed hostname can leave the old one behind on the tunnel, where it reads as foreign.
      qc.invalidateQueries({ queryKey: ['cloudflare-foreign-routes'] })
      // Designating or releasing a login host changes what the realm roster reports as its login host.
      qc.invalidateQueries({ queryKey: ['realms'] })
      // The saved policy and the rule attachment counts both describe what was just written.
      qc.invalidateQueries({ queryKey: ['route-access'] })
      qc.invalidateQueries({ queryKey: ['access-rules'] })
      closeForm()
    },
    onError: (err: Error) => toast.error(err.message),
  })

  /** Closes the form, and forgets the route it was editing so the next open starts from a blank create. */
  function closeForm() {
    setShowForm(false)
    if (editingRoute) {
      setEditingRoute(null)
      setForm({ ...emptyForm })
    }
    // Whatever was changed in the access editor belonged to the route being edited.
    setEditAccessDraft(null)
    dns.reset()
  }

  /**
   * Opens the form for a new route. Coming out of an edit it starts blank; otherwise it keeps whatever was
   * typed before the form was last closed, which is how Cancel has always behaved.
   */
  function openCreate() {
    if (editingRoute) {
      setEditingRoute(null)
      setForm({ ...emptyForm })
      dns.reset()
    }
    setShowForm(true)
  }

  /**
   * Loads an existing route into the form (the counterpart of {@link startImport}, which does the same for a
   * hostname the route table does not know yet). Every field is spelled the way the create half would have
   * produced it, so saving an untouched form sends back exactly what is stored.
   */
  function startEdit(route: Route) {
    // Same reasoning as an import: where a primary domain covers the hostname the composed control can hold
    // it; where none does, the custom field is the only place it fits.
    const split = route.domain ? splitHost(primaryNames, route.domain) : null
    setEditingRoute(route)
    // Starts from the stored policy each time, as it arrives — not from another route's unsaved changes.
    setEditAccessDraft(null)
    setForm({
      ...emptyForm,
      binding: route.binding,
      target: route.target,
      realmId: route.realmId != null ? String(route.realmId) : '',
      makeLoginRoute: route.isLoginRoute,
      stackId: route.stackId != null ? String(route.stackId) : '',
      domain: route.domain ?? '',
      subdomain: split?.subdomain ?? '',
      primaryDomain: split?.primaryDomain ?? '',
      customHostname: route.domain != null && split === null,
      serviceName: route.target === 'watchtower' ? '' : route.serviceName,
      containerPort: route.target === 'watchtower' ? '' : String(route.containerPort),
      listenPort: route.listenPort != null ? String(route.listenPort) : '',
      tlsEnabled: route.tlsEnabled,
      // The stored service and port stay editable as text even when the stack's containers are not
      // running right now, which is exactly when discovery would offer nothing to pick from.
      serviceManual: true,
      portManual: true,
    })
    dns.reset()
    setShowForm(true)
    window.scrollTo({ top: 0, behavior: 'smooth' })
  }

  /** Prefills the new-route form from a dashboard-made tunnel hostname and opens it. */
  function startImport(foreign: CloudflareForeignRoute) {
    setEditingRoute(null)
    // The hostname exists already, so the form has to show it however it was spelled. Where a primary
    // domain covers it the composed control can hold it and the operator sees the same shape they get
    // for a new route; where none does, the custom field is the only place it fits.
    const split = splitHost(primaryNames, foreign.hostname)
    setForm({
      ...emptyForm,
      domain: foreign.hostname,
      subdomain: split?.subdomain ?? '',
      primaryDomain: split?.primaryDomain ?? '',
      customHostname: split === null,
      stackId: foreign.suggestedStackId != null ? String(foreign.suggestedStackId) : '',
      serviceName: foreign.suggestedServiceName ?? '',
      containerPort: foreign.suggestedContainerPort != null ? String(foreign.suggestedContainerPort) : '',
      // Manual mode keeps the prefilled values editable as text even before container discovery
      // resolves (the suggestion may name a service that isn't running right now).
      serviceManual: true,
      portManual: true,
    })
    setShowForm(true)
    window.scrollTo({ top: 0, behavior: 'smooth' })
  }

  // Under Cloudflare a delete is a choice: unown the hostname (its tunnel rule and DNS record stay and
  // it reappears as importable) or remove it from Cloudflare too. Off by default — the conservative one.
  const [removeFromCloudflare, setRemoveFromCloudflare] = useState(false)
  const remove = useMutation({
    mutationFn: ({ route, removeFromProvider }: { route: Route; removeFromProvider: boolean }) =>
      api.proxy.deleteRoute(route.id, removeFromProvider),
    onSuccess: (result, { route, removeFromProvider }) => {
      toast.success(
        removeFromProvider
          ? `Deleted ${routeLabel(route)} and removed it from Cloudflare.`
          : `Deleted ${routeLabel(route)}.`,
      )
      // Deleting a realm's login host is allowed and has a consequence the operator has to hear about:
      // that realm's protected apps stop redirecting anywhere until another one is designated.
      if (result?.warning) toast.error(result.warning)
      qc.invalidateQueries({ queryKey: ['routes'] })
      // A deleted port route is a published host port with nothing behind it — which is the other half
      // of what the banner offers to do.
      qc.invalidateQueries({ queryKey: ['proxy', 'port-bindings'] })
      // An unowned hostname is foreign again (and a removed one is gone) — either way the card changes.
      qc.invalidateQueries({ queryKey: ['cloudflare-foreign-routes'] })
    },
    onError: (err: Error, { route }) => {
      toast.error(`Failed to delete ${routeLabel(route)}: ${err.message}`)
      // A cleanup failure still deleted the route row; show the table as it now is.
      qc.invalidateQueries({ queryKey: ['routes'] })
      qc.invalidateQueries({ queryKey: ['cloudflare-foreign-routes'] })
    },
    onSettled: () => {
      setPendingDelete(null)
      setRemoveFromCloudflare(false)
    },
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()

    // A port route has no hostname at all, and every field that describes one is refused rather than
    // ignored — so the domain half of the form is not sent, and not checked either.
    if (isPortForm) {
      const stackId = Number(form.stackId)
      const containerPort = Number(form.containerPort)
      const listenPort = Number(form.listenPort)
      // Only when the settings actually said so; where they could not be read the server's own refusal
      // is the honest answer, rather than a guess made from a query that failed.
      if (lanNamesKnown && lanNames.length === 0)
        return toast.error('Set the LAN names under Settings → Reverse proxy first.')
      if (!stackId) return toast.error('Choose a stack.')
      if (!form.serviceName.trim()) return toast.error('Enter a service name.')
      if (!containerPort || containerPort < 1 || containerPort > 65535)
        return toast.error('Enter a valid container port (1–65535).')
      if (!listenPort || listenPort < 1 || listenPort > 65535)
        return toast.error('Enter a valid listen port (1–65535).')
      if (editingRoute) {
        return update.mutate({
          id: editingRoute.id,
          data: {
            // Sent back for confirmation only: the binding is fixed, and the server refuses another one.
            binding: 'port',
            domain: null,
            serviceName: form.serviceName.trim(),
            containerPort,
            listenPort,
            tlsEnabled: true,
            // Round-tripped, never re-derived: this form has no control for it, so anything else it sent
            // would be a silent change the operator never asked for.
            isPrimary: editingRoute.isPrimary,
          },
        })
      }
      return create.mutate({
        binding: 'port',
        target: 'service',
        stackId,
        domain: null,
        serviceName: form.serviceName.trim(),
        containerPort,
        listenPort,
        // Fixed by what a port route is, and settled by the server either way.
        tlsEnabled: true,
        isPrimary: false,
      })
    }

    if (!composed) return toast.error('Enter a domain.')

    // Which primary domain the hostname sits under, said as the route's kind (ADR-0036). Null while no
    // primary domains are configured at all: with nothing to be covered by, `custom` would be a claim
    // about a hostname nobody has classified, so the server's own default decides instead.
    const kind: DomainKind | null =
      primaryNames.length === 0 ? null : bestPrimaryDomain(primaryNames, composed) ? 'managed' : 'custom'

    // On an edit the TLS flag is sent as stored rather than forced on under Cloudflare, where the switch is
    // hidden and the flag decides nothing: forcing it would quietly rewrite a setting that starts mattering
    // again the moment the provider is switched back.
    const tlsEnabled = editingRoute ? form.tlsEnabled : isCloudflare || form.tlsEnabled

    // A Watchtower route has no stack, no service and no port — the server refuses them rather than
    // ignoring them, so they are not sent at all.
    if (isWatchtowerForm) {
      if (editingRoute) {
        return update.mutate({
          id: editingRoute.id,
          data: {
            domain: composed,
            kind,
            serviceName: '',
            containerPort: 0,
            tlsEnabled,
            isPrimary: editingRoute.isPrimary,
            // Designates this route as the realm's login host, or releases it if it was one.
            makeLoginRoute: form.makeLoginRoute,
          },
        })
      }
      return create.mutate({
        target: 'watchtower',
        realmId: formRealmId,
        makeLoginRoute: form.makeLoginRoute,
        stackId: 0,
        domain: composed,
        kind,
        serviceName: '',
        containerPort: 0,
        tlsEnabled,
        isPrimary: false,
      })
    }

    const stackId = Number(form.stackId)
    const containerPort = Number(form.containerPort)
    if (!stackId) return toast.error('Choose a stack.')
    if (!form.serviceName.trim()) return toast.error('Enter a service name.')
    if (!containerPort || containerPort < 1 || containerPort > 65535)
      return toast.error('Enter a valid container port (1–65535).')
    if (editingRoute) {
      // The access policy goes in the same request, so the route and who reaches it are saved as one write.
      // Only when the operator actually changed it: otherwise nothing is sent, which the server reads as
      // "leave access alone" — so renaming a route neither rewrites its policy nor leaves an audit row for an
      // access change nobody made, and a non-administrator's edit means what it always has.
      const access = wantsAccessEditor && editAccessDraft ? toRouteAccess(editAccessDraft) : null
      return update.mutate({
        id: editingRoute.id,
        data: {
          binding: 'domain',
          domain: composed,
          kind,
          serviceName: form.serviceName.trim(),
          containerPort,
          tlsEnabled,
          isPrimary: editingRoute.isPrimary,
          ...(access && {
            accessMode: access.mode,
            bypassPaths: access.bypassPaths,
            identityHeaderMode: access.identityHeaderMode,
            grantedUserIds: access.grantedUserIds,
            grantedGroupIds: access.grantedGroupIds,
            accessRuleIds: access.accessRuleIds,
          }),
        },
      })
    }
    // The whole policy, normalized the way an edit sends it: only what the chosen mode uses.
    const access = toRouteAccess({
      mode: formAccessMode,
      identityHeaderMode: form.identityHeaderMode,
      bypassPaths: form.bypassPaths,
      grantedUserIds: form.grantedUserIds,
      grantedGroupIds: form.grantedGroupIds,
      accessRuleIds: form.accessRuleIds,
    })
    const accessRuleIds = access.accessRuleIds ?? []
    const namesDetail =
      access.grantedUserIds.length > 0 ||
      access.grantedGroupIds.length > 0 ||
      accessRuleIds.length > 0 ||
      access.identityHeaderMode !== 'None'
    create.mutate({
      target: 'service',
      stackId,
      domain: composed,
      kind,
      serviceName: form.serviceName.trim(),
      containerPort,
      tlsEnabled,
      isPrimary: false,
      // Naming any part of the policy is admin-only, and an untouched mode means "use the configured
      // default" — so everything stays null unless an administrator picked it. Once grants or rules are
      // chosen the mode is sent explicitly too: they were chosen *for* that mode, and a default that changed
      // between render and submit must not pair them with another one.
      accessMode: canManageAccess ? (form.accessMode || (namesDetail ? formAccessMode : null)) : null,
      bypassPaths: canManageAccess ? access.bypassPaths : null,
      identityHeaderMode:
        canManageAccess && access.mode !== 'Public' && access.identityHeaderMode !== 'None'
          ? access.identityHeaderMode
          : null,
      grantedUserIds: canManageAccess && access.grantedUserIds.length > 0 ? access.grantedUserIds : null,
      grantedGroupIds: canManageAccess && access.grantedGroupIds.length > 0 ? access.grantedGroupIds : null,
      accessRuleIds: canManageAccess && accessRuleIds.length > 0 ? accessRuleIds : null,
    })
  }

  /**
   * What the tooltip beside a port route's address says. Four states, and only one of them is a failure:
   * names to list, a read that came back empty, a read that failed, and no read at all — the last while
   * the provider is still being fetched, or under a provider that has no port listener to describe.
   */
  const portAddressNote = (r: Route) => {
    if (lanNames.length > 0)
      return `Also reachable on ${lanNames.map((n) => `${authorityHost(n)}:${r.listenPort}`).join(', ')} — the certificate carries every LAN name.`
    // Same distinction the form makes: no names and no answer are different states, and sending an
    // operator to configure what may already be there is the wrong one to guess.
    if (lanNamesKnown)
      return 'Set the LAN names under Settings → Reverse proxy to give this port an address.'
    if (lanNamesUnavailable)
      return 'The proxy settings could not be read, so the address this port is reached at cannot be shown.'
    // Nothing failed here: the settings were never asked for. Saying they could not be read would
    // report a fault at first paint, and would stand permanently while the proxy is off.
    if (!supportsPortRoutes)
      return `Port ${r.listenPort}. The reverse proxy is off, so nothing is listening on it.`
    return `Port ${r.listenPort}. The address it is reached at is not known until the proxy settings load.`
  }

  /**
   * The route's address, as a link where one can be built. A port route is shown as
   * `https://{first LAN name}:{port}`; the tooltip names the rest, because every configured LAN name
   * reaches it and the certificate carries all of them.
   */
  const renderAddress = (r: Route) => {
    const href = routeUrl(r, lanNames, servesHttps(r))
    const label =
      r.binding === 'port'
        ? firstLanName
          ? `${authorityHost(firstLanName)}:${r.listenPort}`
          : `port ${r.listenPort}`
        : (r.domain ?? routeLabel(r))
    const link = href ? (
      <a
        href={href}
        target="_blank"
        rel="noreferrer"
        className="inline-flex items-center gap-1.5 font-medium text-text hover:text-brand"
      >
        {label}
        <ExternalLink className="size-3.5 text-text-3" />
      </a>
    ) : (
      // No LAN name to build one from: there is no address to link to, and saying so beats a dead link.
      <span className="font-medium text-text">{label}</span>
    )

    if (r.binding !== 'port') return link
    return <Tooltip label={portAddressNote(r)}>{link}</Tooltip>
  }

  /**
   * Whether this row is a port route whose host port the container does not publish. Only ever true on a
   * positive answer — hence the `portsKnown` gate, which is not redundant with the lookup below so much
   * as the statement of the rule: while the query is loading, and wherever Watchtower cannot see its own
   * container, nothing is claimed about this row. An unadorned row beats one accused of being broken.
   */
  const isUnpublished = (r: Route) =>
    portsKnown &&
    r.binding === 'port' &&
    r.listenPort != null &&
    bindingByPort.get(r.listenPort)?.bound === false

  // Why the port cannot be published, when the answer is another container holding it. The server
  // phrases it — it is the same sentence the publish and the route form refuse with — so the row, the
  // button and the validation message never tell three different stories about one port.
  const blockedBy = (r: Route) =>
    (r.listenPort != null ? bindingByPort.get(r.listenPort)?.blockedBy : null) ?? null

  const unpublishedBadge = (r: Route) =>
    isUnpublished(r) ? (
      <Tooltip
        label={
          blockedBy(r) ??
          `Watchtower listens on ${r.listenPort} inside its container, but the container does not publish that host port — nothing on the network can reach this route yet.`
        }
      >
        <Badge tone="warn">
          {blockedBy(r) ? 'host port held by another container' : 'host port not published'}
        </Badge>
      </Tooltip>
    ) : null

  /**
   * Who gets through this route. Shown on every row, port and Watchtower ones included: those are always
   * Public and reading "Public" there is the truth, not an omission.
   */
  const accessBadge = (r: Route) => (
    <Tooltip label={ACCESS_DESCRIPTION[r.accessMode]}>
      <Badge tone={ACCESS_TONE[r.accessMode]}>{ACCESS_LABEL[r.accessMode]}</Badge>
    </Tooltip>
  )

  /**
   * The route list cut into sections, one per primary domain that has routes, then the domain routes no
   * primary covers, then the LAN ports (ADR-0036). Grouping is by hostname suffix rather than by
   * `Route.kind`: every row created before primary domains existed is `managed` whatever it is named, so
   * kind would put a hostname under a heading its own name contradicts. Empty groups are dropped — a
   * "LAN ports" heading over nothing describes a feature, not this deployment.
   */
  const groups = useMemo(() => {
    const domainRoutes = routes.filter((r) => r.binding !== 'port')
    const byPrimary = new Map<string, Route[]>()
    const other: Route[] = []
    for (const route of domainRoutes) {
      const primary = route.domain ? bestPrimaryDomain(primaryNames, route.domain) : null
      if (primary === null) {
        other.push(route)
        continue
      }
      const bucket = byPrimary.get(primary)
      if (bucket) bucket.push(route)
      else byPrimary.set(primary, [route])
    }

    const result: { key: string; title: string; subtitle?: string; routes: Route[] }[] = []
    // The server's order, which is by name — so the sections do not reshuffle between renders.
    for (const domain of primaryDomains) {
      const inDomain = byPrimary.get(domain.name)
      if (!inDomain || inDomain.length === 0) continue
      result.push({
        key: `primary:${domain.name}`,
        title: domain.name,
        // Only for a domain nobody typed: where it came from is the question a discovered zone raises
        // and a configured one does not.
        subtitle: domain.source === 'cloudflare-zone' ? domain.detail : undefined,
        // Apex first — the domain itself is the heading's own row — then alphabetically by subdomain.
        routes: [...inDomain].sort((a, b) => {
          const subA = subdomainOf(domain.name, a.domain ?? '') ?? ''
          const subB = subdomainOf(domain.name, b.domain ?? '') ?? ''
          return subA.localeCompare(subB)
        }),
      })
    }
    if (other.length > 0) result.push({ key: 'other', title: 'Other domains', routes: other })
    if (portRoutes.length > 0) result.push({ key: 'ports', title: 'LAN ports', routes: portRoutes })
    return result
  }, [routes, portRoutes, primaryDomains, primaryNames])

  // One flat table below one heading is what a single group would render anyway, and it is the shape
  // that carries the empty state and the loading skeleton — so the sections appear only once there is
  // more than one, and never over a list that has not answered yet.
  const grouped = primaryNames.length > 0 && groups.length > 1 && !isLoading && !isError

  const columns: DataListColumn<Route>[] = [
    {
      key: 'domain',
      header: 'Address',
      cell: renderAddress,
    },
    {
      key: 'stack',
      header: 'Stack',
      cell: (r) =>
        r.target === 'watchtower' ? (
          <div className="flex flex-wrap items-center gap-1.5">
            <Badge tone="brand">Watchtower</Badge>
            {r.isLoginRoute && (
              <Tooltip label={`Anonymous visitors to this realm's protected apps are redirected here.`}>
                <Badge tone="ok">login host ({r.realmSlug ?? `realm ${r.realmId}`})</Badge>
              </Tooltip>
            )}
          </div>
        ) : (
          <div className="flex flex-wrap items-center gap-1.5">
            <span className="text-[13px] text-text-2">{r.stackName ?? `#${r.stackId}`}</span>
            {r.binding === 'port' && (
              <Tooltip label="Served on a TLS port of its own, with a certificate from Watchtower's internal CA. LAN only — there is no public hostname.">
                <Badge tone="neutral">LAN port</Badge>
              </Tooltip>
            )}
            {unpublishedBadge(r)}
          </div>
        ),
    },
    {
      key: 'target',
      header: 'Target',
      cell: (r) =>
        r.target === 'watchtower' ? (
          <span className="text-[13px] text-text-2">this instance</span>
        ) : (
          <span className="font-mono text-[13px] text-text-2">
            {r.serviceName}:{r.containerPort}
          </span>
        ),
    },
    {
      key: 'access',
      header: 'Access',
      cell: (r) => accessBadge(r),
    },
    {
      key: 'tls',
      header: 'TLS',
      cell: (r) => (
        <Badge tone={servesHttps(r) ? 'ok' : 'neutral'}>{servesHttps(r) ? 'HTTPS' : 'HTTP'}</Badge>
      ),
    },
    {
      key: 'status',
      header: 'Status',
      cell: (r) => (
        <Tooltip label={r.statusDetail ?? STATUS_LABEL[r.status]}>
          <Badge tone={STATUS_TONE[r.status]}>{STATUS_LABEL[r.status]}</Badge>
        </Tooltip>
      ),
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      cell: (r) => (
        <div className="flex items-center justify-end gap-1">
          <Tooltip label="Edit route">
            <Button
              size="icon-sm"
              variant="ghost"
              aria-label={`Edit ${routeLabel(r)}`}
              onClick={() => startEdit(r)}
              className="text-text-2 hover:text-text"
            >
              <Pencil />
            </Button>
          </Tooltip>
          <Tooltip label="Delete route">
            <Button
              size="icon-sm"
              variant="ghost"
              aria-label={`Delete ${routeLabel(r)}`}
              onClick={() => setPendingDelete(r)}
              className="text-text-2 hover:text-danger"
            >
              <Trash2 />
            </Button>
          </Tooltip>
        </div>
      ),
    },
  ]

  const renderCard = (r: Route) => (
    <div className="space-y-3">
      <div className="flex items-start justify-between gap-3">
        {renderAddress(r)}
        <Badge tone={STATUS_TONE[r.status]}>{STATUS_LABEL[r.status]}</Badge>
      </div>
      {r.target === 'watchtower' ? (
        <div className="flex flex-wrap items-center gap-1.5 text-[13px] text-text-2">
          <Badge tone="brand">Watchtower</Badge>
          {r.isLoginRoute && <Badge tone="ok">login host ({r.realmSlug ?? `realm ${r.realmId}`})</Badge>}
          {accessBadge(r)}
          <span>· {servesHttps(r) ? 'HTTPS' : 'HTTP'}</span>
        </div>
      ) : (
        <div className="space-y-1.5">
          <p className="text-[13px] text-text-2">
            {r.stackName ?? `#${r.stackId}`} ·{' '}
            <span className="font-mono">
              {r.serviceName}:{r.containerPort}
            </span>{' '}
            · {r.binding === 'port' ? 'HTTPS (LAN port)' : servesHttps(r) ? 'HTTPS' : 'HTTP'}
          </p>
          <div className="flex flex-wrap items-center gap-1.5">
            {accessBadge(r)}
            {unpublishedBadge(r)}
          </div>
        </div>
      )}
      <div className="flex items-center justify-between border-t border-border pt-3">
        <span className="text-xs text-text-3">created {timeAgo(r.createdAt)}</span>
        <div className="flex items-center gap-1">
          <Button
            size="icon-sm"
            variant="ghost"
            aria-label={`Edit ${routeLabel(r)}`}
            onClick={() => startEdit(r)}
            className="text-text-2 hover:text-text"
          >
            <Pencil />
          </Button>
          <Button
            size="icon-sm"
            variant="ghost"
            aria-label={`Delete ${routeLabel(r)}`}
            onClick={() => setPendingDelete(r)}
            className="text-text-2 hover:text-danger"
          >
            <Trash2 />
          </Button>
        </div>
      </div>
    </div>
  )

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Routes</h1>
          {status && (
            <>
              {/* The caveat, not a second status: the badge already says running/starting, and
                  providerDetail is what that verdict is hiding — "bound over plain HTTP only", or
                  how far through first issuance the certificates are. */}
              <Tooltip label={status.providerDetail ?? 'The active provider has nothing to report.'}>
                <Badge tone={status.enabled ? (status.caddyRunning ? 'ok' : 'warn') : 'neutral'}>
                  {status.enabled
                    ? status.caddyRunning
                      ? 'Proxy running'
                      : 'Proxy starting…'
                    : 'Proxy disabled'}
                </Badge>
              </Tooltip>
              {status.providerDetail && (
                <span className="text-[13px] text-text-2">{status.providerDetail}</span>
              )}
            </>
          )}
        </div>
        {/* No longer gated on there being a stack: a Watchtower route has none, and the very first route
            an operator creates is often the one that exposes Watchtower itself. */}
        <Button variant="primary" onClick={() => (showForm ? closeForm() : openCreate())}>
          {showForm ? <X /> : <Plus />} {showForm ? 'Cancel' : 'New route'}
        </Button>
      </div>

      {status && !status.enabled && (
        <Banner tone="warn" title="Reverse proxy is disabled">
          Routes are saved but not served until the proxy is enabled — flip it under Settings →
          Reverse proxy (applies immediately). The built-in provider needs host ports 80 and 443
          published to Watchtower's ingress endpoints (<span className="font-mono">80:8081</span>,{' '}
          <span className="font-mono">443:8443</span>); the Caddy provider needs them free on the
          host; the Cloudflare Tunnel provider needs no open ports.
        </Banner>
      )}

      {/* Not a verdict on the routes — an admission that there is none to give. A bare-metal install
          publishes nothing and works perfectly; so does a container whose Docker socket is momentarily
          unreadable. Both land here, and neither is told its routes are broken. */}
      {portsUnknown && (
        <Banner tone="info" title="Watchtower cannot see its own container">
          So it cannot tell whether the host ports these routes listen on are published, and it cannot
          publish them for you. Make sure each port route's port reaches this process from the network —
          in a container that means a matching{' '}
          <span className="font-mono">
            -p {portRoutes[0]?.listenPort ?? 9001}:{portRoutes[0]?.listenPort ?? 9001}
          </span>{' '}
          and a recreate.
        </Banner>
      )}

      {/* The gap between "the route exists" and "the route is reachable": Watchtower is already
          listening on the port inside its container, and Docker cannot publish a host port on a running
          container — only on a new one. So the fix is a recreate, which is a Watchtower restart, which
          is why it is a button behind a confirmation and never something that just happens. */}
      {hasPendingPorts && (
        <Banner
          tone={pendingPorts.length > 0 ? 'warn' : 'info'}
          title={
            firstPending === undefined
              ? `${stalePorts.length === 1 ? 'A host port is' : `${stalePorts.length} host ports are`} published for no route`
              : pendingPorts.length === 1
                ? `Host port ${firstPending.port} is not published`
                : `${pendingPorts.length} host ports are not published`
          }
          action={
            publishRefusal == null ? (
              <Button variant="link" onClick={() => setConfirmPublish(true)}>
                {publishActionLabel}
              </Button>
            ) : (
              // Disabled rather than hidden: the operator is looking at a route that does not work and
              // should be told why the obvious remedy is not on offer here.
              <Tooltip label={publishRefusal}>
                {/* A disabled button swallows pointer events and can't take focus, so the wrapping span
                    is the trigger — made focusable so keyboard users get the reason too. */}
                <span className="inline-flex" tabIndex={0}>
                  <Button variant="link" disabled>
                    {publishActionLabel}
                  </Button>
                </span>
              </Tooltip>
            )
          }
        >
          {firstPending !== undefined && (
            <>
              Watchtower listens on{' '}
              <span className="font-mono">{pendingPorts.map((p) => p.port).join(', ')}</span> inside its
              container, but the container does not publish{' '}
              {pendingPorts.length === 1 ? 'that host port' : 'those host ports'} — so nothing on the
              network reaches {pendingPorts.length === 1 ? 'that route' : 'those routes'} yet. Watchtower
              can recreate its own container to add {pendingPorts.length === 1 ? 'it' : 'them'}, or add{' '}
              <span className="font-mono">
                -p {firstPending.port}:{firstPending.port}
              </span>{' '}
              yourself and recreate it.{' '}
            </>
          )}
          {stalePorts.length > 0 && (
            <>
              Watchtower still publishes{' '}
              <span className="font-mono">{stalePorts.join(', ')}</span> for{' '}
              {stalePorts.length === 1 ? 'a route' : 'routes'} that no longer{' '}
              {stalePorts.length === 1 ? 'exists' : 'exist'}; the same restart releases{' '}
              {stalePorts.length === 1 ? 'it' : 'them'}. Ports you published yourself are never touched.
            </>
          )}
          {portBindings?.lastError && (
            <>
              {' '}
              The last attempt failed: {portBindings.lastError}
            </>
          )}
        </Banner>
      )}

      {foreignWarning && (
        <Banner tone="warn" title="Cloudflare hostnames not visible">
          {foreignWarning}
        </Banner>
      )}

      {foreignRoutes.length > 0 && (
        <Card>
          <CardContent>
            <SectionHeader
              title={`Found in Cloudflare (${foreignRoutes.length})`}
              description={
                foreignCollapsed
                  ? undefined
                  : "Public hostnames configured in the Cloudflare dashboard, across all of the account's tunnels. Watchtower leaves them untouched — import one to manage it as a route (served from Watchtower's tunnel, with access control, per-stack networking and cleanup on stack removal)."
              }
              className={foreignCollapsed ? 'mb-0 border-b-0 pb-0' : undefined}
              action={
                <Button size="sm" variant="ghost" onClick={toggleForeign}>
                  {foreignCollapsed ? (
                    <>
                      <ChevronDown /> Show
                    </>
                  ) : (
                    <>
                      <ChevronUp /> Hide
                    </>
                  )}
                </Button>
              }
            />
            {!foreignCollapsed && (
            <ul className="divide-y divide-border">
              {foreignRoutes.map((f) => (
                <li key={`${f.tunnelName}/${f.hostname}`} className="flex flex-wrap items-center justify-between gap-2 py-2.5">
                  <div className="min-w-0">
                    <span className="block truncate font-medium text-text">{f.hostname}</span>
                    <span className="block truncate font-mono text-[13px] text-text-2">
                      → {f.service}
                      {f.path ? ` (path ${f.path})` : ''}
                    </span>
                    <span className="block text-xs text-text-3">
                      on tunnel “{f.tunnelName}”
                      {f.suggestedStackName && (
                        <>
                          {' '}· looks like stack “{f.suggestedStackName}”, service{' '}
                          <span className="font-mono">{f.suggestedServiceName}:{f.suggestedContainerPort}</span>
                        </>
                      )}
                    </span>
                  </div>
                  <Button size="sm" variant="secondary" onClick={() => startImport(f)}>
                    <CloudDownload /> Import
                  </Button>
                </li>
              ))}
            </ul>
            )}
          </CardContent>
        </Card>
      )}

      {showForm && (
        <Card>
          <CardContent>
            <SectionHeader
              title={editingRoute ? `Edit route · ${routeLabel(editingRoute)}` : 'New route'}
              description={
                editingRoute
                  ? 'Change where this route points and how it is served. What it is — its binding, what serves it and which stack or realm it belongs to — is fixed once created; delete and recreate the route to change that, so a live address is never moved by an edit.'
                  : 'Point a domain at a service inside a stack, or at Watchtower itself. HTTPS is provisioned automatically, and new domain routes are protected by default.'
              }
            />
            <form onSubmit={submit} className="space-y-4">
              {/* Shown instead of the choice when editing: the binding is fixed (ADR-0033), and a radio that
                  could only refuse would look like an option. */}
              {isEditing && supportsPortRoutes && (
                <p className="text-[13px] text-text-2">
                  Reached by{' '}
                  <span className="text-text">
                    {ROUTE_BINDINGS.find((b) => b.value === form.binding)?.label ?? form.binding}
                  </span>
                  .
                </p>
              )}

              {supportsPortRoutes && !isEditing && (
                <Field
                  label="How it is reached"
                  required
                  hint={ROUTE_BINDINGS.find((b) => b.value === form.binding)?.description}
                >
                  {() => (
                    <div className="grid gap-2 sm:grid-cols-2">
                      {ROUTE_BINDINGS.map((b) => (
                        <label
                          key={b.value}
                          className="flex cursor-pointer items-center gap-3 rounded-md border border-border px-3 py-2 hover:bg-surface-2"
                        >
                          <input
                            type="radio"
                            name="route-binding"
                            checked={form.binding === b.value}
                            // Switching invalidates the other kind's half of the form outright: a port
                            // route has no hostname and a domain route no listener, and carrying either
                            // across would submit a value the server refuses.
                            onChange={() =>
                              setForm((f) => ({
                                ...emptyForm,
                                binding: b.value,
                                stackId: f.stackId,
                                serviceName: f.serviceName,
                                containerPort: f.containerPort,
                                serviceManual: f.serviceManual,
                                portManual: f.portManual,
                              }))
                            }
                            className="size-4 shrink-0 accent-[var(--brand)]"
                          />
                          <span className="text-sm text-text">{b.label}</span>
                        </label>
                      ))}
                    </div>
                  )}
                </Field>
              )}

              {isPortForm && lanNamesUnavailable && (
                <Banner
                  tone="danger"
                  title="Couldn’t read the proxy settings"
                  action={
                    <Button variant="link" onClick={() => proxyConfigQuery.refetch()}>
                      Retry
                    </Button>
                  }
                >
                  The LAN names a port route's certificate is issued for could not be read, so this form
                  cannot tell whether any are configured.{' '}
                  {(proxyConfigQuery.error as Error)?.message ?? 'An unexpected error occurred.'}
                </Banner>
              )}

              {isPortForm && lanNamesKnown && lanNames.length === 0 && (
                <Banner tone="warn" title="No LAN names configured">
                  A port route's certificate is issued for the names and IPs you type in the browser, so
                  there has to be at least one. Add them under Settings → Reverse proxy (“LAN names”),
                  where suggestions are offered, so you may not have to type them.
                </Banner>
              )}

              {!isPortForm && (
              <Field
                label="Serve this domain with"
                required
                hint={
                  isEditing
                    ? 'Fixed once created — a Watchtower route and a service route are different kinds of route.'
                    : ROUTE_TARGETS.find((t) => t.value === form.target)?.description
                }
              >
                {({ id, describedBy }) => (
                  <Select
                    disabled={isEditing}
                    value={form.target}
                    onValueChange={(v) =>
                      // Switching target invalidates the other half of the form outright: a Watchtower
                      // route has no stack and a service route has no realm, and carrying either across
                      // would submit a value the server refuses.
                      //
                      // Only an actual switch, though. Radix re-announces a value set programmatically
                      // (loading a route into the form sets it), and treating that as a switch would reset
                      // everything else — turning "use as login host" back on for a Watchtower route that
                      // is not one, which the next save would then quietly make it.
                      setForm((f) => v === f.target ? f : ({
                        ...emptyForm,
                        domain: f.domain,
                        // The hostname is the one thing both targets have, so all three fields that
                        // spell it come across — dropping them would reset a composed domain to the
                        // first primary domain's apex mid-form.
                        subdomain: f.subdomain,
                        primaryDomain: f.primaryDomain,
                        customHostname: f.customHostname,
                        tlsEnabled: f.tlsEnabled,
                        target: v as RouteTarget,
                      }))
                    }
                  >
                    <SelectTrigger id={id} aria-describedby={describedBy}>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {ROUTE_TARGETS.map((t) => (
                        <SelectItem key={t.value} value={t.value}>
                          {t.label}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                )}
              </Field>
              )}

              {isPortForm ? (
                <Field
                  label="Listen port"
                  required
                  hint={
                    firstLanName
                      ? `Reached at https://${authorityHost(firstLanName)}:${form.listenPort || '9001'} — and on every other LAN name you configured.`
                      : 'The host port this route answers on.'
                  }
                >
                  {({ id, describedBy }) => (
                    <Input
                      id={id}
                      aria-describedby={describedBy}
                      mono
                      type="number"
                      min={1}
                      max={65535}
                      value={form.listenPort}
                      onChange={(e) => setForm((f) => ({ ...f, listenPort: e.target.value }))}
                      placeholder="9001"
                      autoComplete="off"
                    />
                  )}
                </Field>
              ) : (
                <>
              {/* Two shapes of the same field, and the same label over both: the hostname composed out
                  of a subdomain and one of the configured primary domains, or typed in full. Which one
                  is shown depends on whether there is anything to compose against (ADR-0036). */}
              {primaryNames.length > 0 && !form.customHostname ? (
                <Field
                  label="Domain"
                  required
                  hint={`Leave the subdomain empty to use ${form.primaryDomain || firstPrimaryName} itself.`}
                >
                  {({ id, describedBy }) => (
                    <>
                      <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
                        <div className="flex flex-1 items-center gap-1.5">
                          <Input
                            id={id}
                            aria-describedby={describedBy}
                            mono
                            value={form.subdomain}
                            onChange={(e) => setForm((f) => ({ ...f, subdomain: e.target.value }))}
                            placeholder="app"
                            autoComplete="off"
                            spellCheck={false}
                            className="flex-1"
                          />
                          <span className="shrink-0 font-mono text-sm text-text-2">.</span>
                          {/* With one primary domain there is nothing to choose, so the suffix is shown
                              as text rather than as a dropdown holding a single option. */}
                          {primaryNames.length === 1 ? (
                            <span className="shrink-0 font-mono text-sm text-text-2">
                              {firstPrimaryName}
                            </span>
                          ) : (
                            <Select
                              value={form.primaryDomain || firstPrimaryName}
                              onValueChange={(v) => setForm((f) => ({ ...f, primaryDomain: v }))}
                            >
                              <SelectTrigger className="w-auto shrink-0 font-mono" aria-label="Primary domain">
                                <SelectValue />
                              </SelectTrigger>
                              <SelectContent>
                                {primaryNames.map((name) => (
                                  <SelectItem key={name} value={name}>
                                    {name}
                                  </SelectItem>
                                ))}
                              </SelectContent>
                            </Select>
                          )}
                        </div>
                        <Button
                          type="button"
                          variant="secondary"
                          loading={dns.isPending}
                          disabled={!composed}
                          onClick={() => dns.mutate(composed)}
                          className="shrink-0"
                        >
                          Check DNS
                        </Button>
                      </div>
                      {/* Primary domains are an affordance, never a restriction: a hostname none of
                          them covers is still a route, and this is the way to it. */}
                      <button
                        type="button"
                        onClick={() =>
                          setForm((f) => ({ ...f, customHostname: true, domain: composed }))
                        }
                        className="self-start text-[13px] text-brand hover:underline"
                      >
                        Use a custom hostname
                      </button>
                    </>
                  )}
                </Field>
              ) : (
              <Field label="Domain" required hint="e.g. app.example.com — point its DNS at this host">
                {({ id, describedBy }) => (
                  <>
                    <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
                      <Input
                        id={id}
                        aria-describedby={describedBy}
                        mono
                        value={form.domain}
                        onChange={(e) => setForm((f) => ({ ...f, domain: e.target.value }))}
                        placeholder="app.example.com"
                        autoComplete="off"
                        spellCheck={false}
                        className="flex-1"
                      />
                      <Button
                        type="button"
                        variant="secondary"
                        loading={dns.isPending}
                        disabled={!composed}
                        onClick={() => dns.mutate(composed)}
                        className="shrink-0"
                      >
                        Check DNS
                      </Button>
                    </div>
                    {/* The way back, offered only when there is something to go back to. What was
                        typed is carried across when a primary domain covers it, so switching does not
                        silently discard a hostname that fits perfectly well. */}
                    {primaryNames.length > 0 && (
                      <button
                        type="button"
                        onClick={() =>
                          setForm((f) => {
                            const split = splitHost(primaryNames, f.domain)
                            return {
                              ...f,
                              customHostname: false,
                              subdomain: split?.subdomain ?? f.subdomain,
                              primaryDomain: split?.primaryDomain ?? f.primaryDomain,
                            }
                          })
                        }
                        className="self-start text-[13px] text-brand hover:underline"
                      >
                        Choose from your domains
                      </button>
                    )}
                  </>
                )}
              </Field>
              )}

              {dns.data && (
                <p className={`text-[13px] ${dns.data.resolves ? 'text-ok' : 'text-warn'}`}>
                  {dns.data.resolves
                    ? `Resolves to ${dns.data.addresses.join(', ')}. Make sure that points at this host.`
                    : 'Does not resolve yet — add a DNS record pointing this domain at your server.'}
                </p>
              )}
                </>
              )}

              {isWatchtowerForm ? (
                <div className="space-y-4">
                  <Field
                    label="Realm"
                    required
                    hint={
                      isEditing
                        ? 'Fixed once created — the realm is whose sign-in cookies this hostname holds.'
                        : 'Whose login page and portal this hostname serves.'
                    }
                  >
                    {({ id, describedBy }) => (
                      <Select
                        disabled={isEditing}
                        value={String(formRealmId)}
                        onValueChange={(v) => setForm((f) => ({ ...f, realmId: v }))}
                      >
                        <SelectTrigger id={id} aria-describedby={describedBy}>
                          <SelectValue placeholder="Choose a realm" />
                        </SelectTrigger>
                        <SelectContent>
                          {realms.map((r) => (
                            <SelectItem key={r.id} value={String(r.id)}>
                              {r.name}
                              {r.isSystem ? ' (operator)' : ''}
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                    )}
                  </Field>

                  <div className="flex items-start justify-between gap-4">
                    <div className="min-w-0">
                      <Label htmlFor="route-login-host">Use as this realm's login host</Label>
                      <p className="mt-1 text-xs text-text-3">
                        {formRealm?.loginHost
                          ? `Anonymous visitors to this realm's protected apps are redirected here instead of ${formRealm.loginHost}.`
                          : "Anonymous visitors to this realm's protected apps are redirected here."}
                      </p>
                    </div>
                    <Switch
                      id="route-login-host"
                      checked={form.makeLoginRoute}
                      onCheckedChange={(on) => setForm((f) => ({ ...f, makeLoginRoute: on }))}
                    />
                  </div>

                  <Banner tone="warn" title="This publishes Watchtower">
                    Serves the Watchtower UI and API on this domain. {WATCHTOWER_ACCESS_NOTE} With
                    authentication disabled this publishes the management UI to anyone who can reach the
                    domain — enable authentication first, under Settings → Authentication.
                  </Banner>
                </div>
              ) : (
              <div className="grid gap-4 md:grid-cols-2">
                <Field
                  label="Stack"
                  required
                  hint={isEditing ? 'Fixed once created — the route lives on this stack’s ingress network.' : undefined}
                >
                  {({ id, describedBy }) => (
                    <Select
                      disabled={isEditing}
                      value={form.stackId}
                      onValueChange={(v) =>
                        // Switching stacks invalidates the service/port chosen for the old one — but only a
                        // switch: Radix re-announces a programmatically set value, and loading a route or an
                        // imported hostname into the form sets exactly this, with the service and port
                        // alongside it that clearing here would throw away.
                        setForm((f) => v === f.stackId ? f : ({
                          ...f,
                          stackId: v,
                          // Grants and rules are realm-scoped, and another stack can be another realm's.
                          // Cleared rather than filtered: the picker below reloads for the new realm.
                          grantedUserIds: [],
                          grantedGroupIds: [],
                          accessRuleIds: [],
                          serviceName: '',
                          containerPort: '',
                          serviceManual: false,
                          portManual: false,
                        }))
                      }
                    >
                      <SelectTrigger id={id} aria-describedby={describedBy}>
                        <SelectValue placeholder="Choose a stack" />
                      </SelectTrigger>
                      <SelectContent>
                        {stacks.map((s) => (
                          <SelectItem key={s.id} value={String(s.id)}>
                            {s.name}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  )}
                </Field>

                <div className="grid grid-cols-2 gap-4">
                  <Field label="Service" required hint="Compose service name">
                    {({ id, describedBy }) => (
                      <ComboField
                        id={id}
                        describedBy={describedBy}
                        value={form.serviceName}
                        onChange={(v) =>
                          setForm((f) => {
                            const next = { ...f, serviceName: v }
                            // Picking a different known service invalidates the previous one's port.
                            if (portsByService.has(v)) {
                              next.containerPort = ''
                              next.portManual = false
                            }
                            return next
                          })
                        }
                        options={serviceOptions}
                        manual={form.serviceManual}
                        onManualChange={(m) => setForm((f) => ({ ...f, serviceManual: m }))}
                        loading={portsLoading}
                        placeholder="Choose a service"
                        inputProps={{
                          mono: true,
                          placeholder: 'web',
                          autoComplete: 'off',
                          spellCheck: false,
                        }}
                      />
                    )}
                  </Field>
                  <Field label="Port" required hint="Container port">
                    {({ id, describedBy }) => (
                      <ComboField
                        id={id}
                        describedBy={describedBy}
                        value={form.containerPort}
                        onChange={(v) => setForm((f) => ({ ...f, containerPort: v }))}
                        options={portOptions}
                        manual={form.portManual}
                        onManualChange={(m) => setForm((f) => ({ ...f, portManual: m }))}
                        loading={portsLoading}
                        placeholder="Choose a port"
                        inputProps={{ mono: true, type: 'number', min: 1, max: 65535, placeholder: '3000' }}
                      />
                    )}
                  </Field>
                </div>
              </div>
              )}

              {/* Said on an edit, where an administrator would look for the gate on this address: it has
                  none, and that is by definition rather than an omission. (The create form's Watchtower and
                  port banners say the same thing at the moment the kind is chosen.) */}
              {isEditing && canManageAccess && editingRoute && accessNote(editingRoute) && (
                <p className="text-xs text-text-3">{accessNote(editingRoute)}</p>
              )}

              {/* Only on a service route to a domain: a port route is LAN-only and a Watchtower route
                  serves the login page, so both are Public by definition and the server refuses an
                  access field on them. Admin-only, like every other access control on this page. */}
              {wantsAccessEditor && (
                <>
                  {/* The one access editor, for a new route and an existing one alike (ADR-0039, decision 5
                      as amended): a route is created with its whole policy and edited the same way, saved in
                      the same write as its other fields — never published under one policy and then
                      changed, and never half-applied by a second call. */}
                  {isEditing ? (
                    editDraft ? (
                      <AccessFields
                        value={editDraft}
                        onChange={setEditAccessDraft}
                        realmName={accessRealmId != null ? (realms.find((r) => r.id === accessRealmId)?.name ?? null) : null}
                        users={accessUsers}
                        groups={accessGroups}
                        accessRules={accessRules}
                        activeEnforcementPoint={accessEnforcementPoint}
                      />
                    ) : (
                      <p className="text-[13px] text-text-3">Loading this route's access policy…</p>
                    )
                  ) : (
                    <AccessFields
                      value={{
                        mode: formAccessMode,
                        identityHeaderMode: form.identityHeaderMode,
                        bypassPaths: form.bypassPaths,
                        grantedUserIds: form.grantedUserIds,
                        grantedGroupIds: form.grantedGroupIds,
                        accessRuleIds: form.accessRuleIds,
                      }}
                      onChange={(next) =>
                        setForm((f) => ({
                          ...f,
                          // Recorded only once it differs from what is shown: an untouched mode stays "the
                          // configured default", which is what a create that names nothing is asking for.
                          accessMode: next.mode === (f.accessMode || defaultAccessMode) ? f.accessMode : next.mode,
                          identityHeaderMode: next.identityHeaderMode,
                          bypassPaths: next.bypassPaths,
                          grantedUserIds: next.grantedUserIds,
                          grantedGroupIds: next.grantedGroupIds,
                          accessRuleIds: next.accessRuleIds,
                        }))
                      }
                      realmName={accessRealmId != null ? (realms.find((r) => r.id === accessRealmId)?.name ?? null) : null}
                      users={accessUsers}
                      groups={accessGroups}
                      accessRules={accessRules}
                      activeEnforcementPoint={accessEnforcementPoint}
                      pickersNote={
                        createStackId == null
                          ? 'Choose a stack first — users, groups and access rules come from the realm it belongs to.'
                          : accessRealmId == null
                            ? 'Loading…'
                            : null
                      }
                    />
                  )}

                  {/* Only where the instance-wide settings *are* this route's allow-list — Authenticated with
                      no rules attached. Rules decide an Authenticated route that attaches them, and grants
                      decide a Restricted one. */}
                  {(isEditing
                    ? editDraft?.mode === 'Authenticated' && editDraft.accessRuleIds.length === 0
                    : formAccessMode === 'Authenticated' && form.accessRuleIds.length === 0) &&
                    cfAllowSourceMissing && (
                    <Banner tone="warn" title="Cloudflare Access has no allow source">
                      With no access rule ticked, this route's Access application admits the emails, email
                      domains, Access groups and reusable policies configured under Settings → Reverse proxy
                      — and none are set, so it would deny everyone. Tick an access rule, add an allow source
                      there, or make this route Public.
                    </Banner>
                  )}
                </>
              )}

              {isPortForm && isEditing ? (
                <p className="text-xs text-text-3">
                  HTTPS is always on, with a certificate from Watchtower's internal CA. Moving the listen
                  port means publishing the new one on Watchtower's container — the banner above offers to
                  do that once the change is saved.
                </p>
              ) : isPortForm ? (
                <Banner tone="info" title="Publish the port on Watchtower's container">
                  Watchtower listens on this port inside its container, so the container has to publish
                  it too. Once the route exists you can do that from here — a banner offers to recreate
                  Watchtower's container with the port added, a restart of a few seconds. The manual
                  equivalent is{' '}
                  <span className="font-mono">
                    -p {form.listenPort || '9001'}:{form.listenPort || '9001'}
                  </span>{' '}
                  (or the matching <span className="font-mono">ports:</span> entry) and a recreate. Until
                  then nothing can connect. HTTPS is always on, with a certificate from Watchtower's
                  internal CA — download its root below and import it once per device.
                </Banner>
              ) : isCloudflare ? (
                <p className="text-xs text-text-3">
                  Served over HTTPS — TLS terminates at Cloudflare's edge, so there is no certificate to
                  manage here.
                </p>
              ) : (
                <div className="flex items-start justify-between gap-4">
                  <div className="min-w-0">
                    <Label htmlFor="route-tls">HTTPS (automatic)</Label>
                    <p className="mt-1 text-xs text-text-3">
                      Terminate TLS with an auto-managed certificate. Turn off to serve plain HTTP.
                    </p>
                  </div>
                  <Switch
                    id="route-tls"
                    checked={form.tlsEnabled}
                    onCheckedChange={(on) => setForm((f) => ({ ...f, tlsEnabled: on }))}
                  />
                </div>
              )}

              <div className="flex justify-end gap-2 pt-1">
                <Button type="button" variant="secondary" onClick={closeForm}>
                  Cancel
                </Button>
                {/* Disabled rather than refused on submit: with no LAN name there is no certificate the
                    route could be served with, and the server refuses it for the same reason. Only
                    once the settings have actually answered — a query still loading is not a
                    deployment with nothing configured, and the server is the backstop either way. */}
                <Button
                  type="submit"
                  loading={isEditing ? update.isPending : create.isPending}
                  disabled={isPortForm && lanNamesKnown && lanNames.length === 0}
                >
                  {isEditing ? 'Save changes' : 'Create route'}
                </Button>
              </div>
            </form>
          </CardContent>
        </Card>
      )}

      {isError && (
        <Banner
          tone="danger"
          title="Couldn’t load routes"
          action={
            <Button variant="link" onClick={() => refetch()}>
              Retry
            </Button>
          }
        >
          {(error as Error)?.message ?? 'An unexpected error occurred.'}
        </Banner>
      )}

      {grouped && (
        <div className="flex flex-col gap-8">
          {groups.map((group) => (
            <div key={group.key}>
              <SectionHeader title={group.title} description={group.subtitle} />
              <DataList
                items={group.routes}
                getKey={(r) => r.id}
                columns={columns}
                renderCard={renderCard}
                aria-label={group.title}
              />
            </div>
          ))}
        </div>
      )}

      {!isError && !grouped && (
        <DataList
          items={routes}
          getKey={(r) => r.id}
          columns={columns}
          renderCard={renderCard}
          skeletonRows={isLoading ? 4 : undefined}
          emptyState={
            <EmptyState
              icon={Globe}
              title="No routes yet"
              description={
                stacks.length === 0
                  ? "Add a route to expose Watchtower itself on a domain, or create a stack first and expose one of its services."
                  : 'Add a route to expose a service — or Watchtower itself — on a domain with automatic HTTPS.'
              }
              action={
                <Button variant="primary" onClick={openCreate}>
                  <Plus /> New route
                </Button>
              }
            />
          }
          aria-label="Routes"
        />
      )}

      {/* Under the routes rather than in Settings: a rule is only meaningful as something a route attaches,
          and the instance-wide allow sources it replaces per route are the Settings half of the same
          decision (ADR-0039). Shown whenever the proxy is on, because a rule outlives a provider switch. */}
      {status?.enabled === true && <AccessRulesCard />}

      {/* Two different questions, and they used to be one. The ACME table is the built-in provider's —
          Caddy and Cloudflare hold their own certificates and Watchtower has none to list — while the
          internal CA belongs to the port routes, which every provider serves. */}
      {status?.provider === 'yarp' && <CertificatesCard />}
      {status?.enabled === true && <InternalCaCard />}

      <ConfirmDialog
        open={pendingDelete != null}
        onOpenChange={(open) => {
          if (!open && !remove.isPending) setPendingDelete(null)
        }}
        title={pendingDelete ? `Delete ${routeLabel(pendingDelete)}?` : 'Delete route?'}
        description={
          pendingDelete?.binding === 'port'
            ? 'The proxy will stop listening on this port. The target container keeps running, and the port stays published on Watchtower’s container until you remove it there.'
            : isCloudflare
              ? 'Watchtower stops managing this domain. The target container keeps running.'
              : 'The proxy will stop serving this domain. The target container keeps running.'
        }
        extra={
          <>
            {/* The one delete whose blast radius reaches past the row: a realm with no login host
                redirects nobody, so its protected apps answer anonymous visitors with 401. */}
            {pendingDelete?.isLoginRoute && (
              <Banner tone="warn" title="This realm will have no login host">
                Anonymous visitors to the protected apps of realm “
                {pendingDelete.realmSlug ?? pendingDelete.realmId}” will get a 401 instead of the login
                page until another Watchtower route is marked as its login host.
              </Banner>
            )}
            {isCloudflare ? (
            <div className="flex items-start justify-between gap-4 rounded-md border border-border p-3">
              <div className="min-w-0">
                <Label htmlFor="route-delete-cf">Also remove from Cloudflare</Label>
                <p className="mt-1 text-xs text-text-3">
                  Deletes the tunnel's ingress rule and the DNS record Watchtower created for this
                  hostname. Off, the hostname stays in Cloudflare as it is and shows up again under
                  “Found in Cloudflare”.
                </p>
              </div>
              <Switch
                id="route-delete-cf"
                checked={removeFromCloudflare}
                onCheckedChange={setRemoveFromCloudflare}
              />
            </div>
            ) : null}
          </>
        }
        confirmLabel={isCloudflare && removeFromCloudflare ? 'Delete everywhere' : 'Delete'}
        tone="danger"
        loading={remove.isPending}
        onConfirm={() => {
          if (pendingDelete)
            remove.mutate({ route: pendingDelete, removeFromProvider: isCloudflare && removeFromCloudflare })
        }}
      />

      <ConfirmDialog
        open={confirmPublish}
        onOpenChange={(open) => {
          if (!open && !publishPorts.isPending) setConfirmPublish(false)
        }}
        title={releaseOnly ? 'Restart Watchtower to release these ports?' : 'Restart Watchtower to publish these ports?'}
        description={
          releaseOnly
            ? 'Watchtower recreates its own container without the ports it published for routes that are gone. It — this page included — is unreachable for a few seconds. A deploy or backup running right now is cancelled by the restart.'
            : 'Watchtower recreates its own container with the ports added. It — this page included — is unreachable for a few seconds, then comes back with the routes working. A deploy or backup running right now is cancelled by the restart.'
        }
        extra={
          // Only where something is being added: a release has nothing for a compose file to lose, and
          // the drift warning would be advice about the opposite of what is about to happen.
          releaseOnly ? null : (
            <Banner tone="info" title="Compose-managed installs">
              A later <span className="font-mono">docker compose up -d</span> rebuilds the container from
              your compose file and drops the ports added here. Mirror them into its{' '}
              <span className="font-mono">ports:</span> list to keep them.
            </Banner>
          )
        }
        confirmLabel={releaseOnly ? 'Release & restart' : 'Publish & restart'}
        loading={publishPorts.isPending}
        onConfirm={() => publishPorts.mutate()}
      />
    </div>
  )
}

// ── Certificates (built-in provider only) ───────────────────────────────────

const CERT_STATE_TONE: Record<CertificateInfo['state'], BadgeTone> = {
  active: 'ok',
  error: 'danger',
  awaitingDns: 'warn',
  pending: 'neutral',
  none: 'neutral',
}

const CERT_STATE_LABEL: Record<CertificateInfo['state'], string> = {
  active: 'Active',
  error: 'Error',
  awaitingDns: 'Awaiting DNS',
  pending: 'Pending',
  none: 'None',
}

const CERT_SOURCE_LABEL: Record<CertificateInfo['source'], string> = {
  route: 'Route',
  internal: 'Internal CA',
  orphan: 'Orphan',
}

/** Relative label that also reads forwards, which "expires in 74d" needs and `timeAgo` cannot do. */
function relativeTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  const ms = new Date(iso).getTime() - Date.now()
  if (Number.isNaN(ms)) return '—'
  return ms >= 0 ? `in ${humanizeSpan(ms)}` : timeAgo(iso)
}

function humanizeSpan(ms: number): string {
  const minutes = Math.floor(ms / 60_000)
  if (minutes < 60) return `${Math.max(minutes, 1)}m`
  const hours = Math.floor(minutes / 60)
  if (hours < 48) return `${hours}h`
  return `${Math.floor(hours / 24)}d`
}

/**
 * What the in-process provider holds, per host. It is the only view of the ACME plane there is: the
 * provider issues for login hosts as well as routes, and keeps a certificate that outlived its route
 * until it expires — neither of which the route list above can show. Rendered only under that provider,
 * since Caddy and the tunnel hold their own certificates and Watchtower has none to list; the internal
 * CA is a separate card because it answers to something else entirely (ADR-0033 addendum).
 */
function CertificatesCard() {
  const qc = useQueryClient()
  const { data: certificates = [], isLoading, isError, error, refetch } = useQuery({
    queryKey: ['proxy-certificates'],
    queryFn: api.proxy.listCertificates,
    // Issuance takes tens of seconds and renewal happens on its own schedule, so the card is worth
    // keeping fresh while it is open — and cheap: the answer is the manager's in-memory snapshot.
    refetchInterval: 30_000,
  })

  const renew = useMutation({
    mutationFn: (host: string) => api.proxy.renewCertificate(host),
    onSuccess: (certificate) => {
      toast.success(`Renewal requested for ${certificate.host}.`)
      qc.invalidateQueries({ queryKey: ['proxy-certificates'] })
      // A fresh certificate is what moves a route from pending to active.
      qc.invalidateQueries({ queryKey: ['routes'] })
      qc.invalidateQueries({ queryKey: ['proxy-status'] })
    },
    onError: (err: Error) => toast.error(err.message || 'Failed to request renewal.'),
  })

  const columns: DataListColumn<CertificateInfo>[] = [
    {
      key: 'host',
      header: 'Host',
      cell: (c) => (
        <div className="min-w-0">
          <span className="block truncate font-medium text-text">{c.host}</span>
          {c.lastError && (
            <span className="block truncate text-xs text-danger" title={c.lastError}>
              {c.lastError}
            </span>
          )}
        </div>
      ),
    },
    { key: 'source', header: 'Source', cell: (c) => CERT_SOURCE_LABEL[c.source] },
    {
      key: 'state',
      header: 'State',
      cell: (c) => <Badge tone={CERT_STATE_TONE[c.state]}>{CERT_STATE_LABEL[c.state]}</Badge>,
    },
    {
      key: 'notAfter',
      header: 'Expires',
      cell: (c) => (
        <span className="text-text-2" title={absoluteTitle(c.notAfter)}>
          {relativeTime(c.notAfter)}
        </span>
      ),
    },
    {
      key: 'nextAttempt',
      header: 'Next attempt',
      cell: (c) => (
        <span className="text-text-2" title={absoluteTitle(c.nextAttemptAt)}>
          {relativeTime(c.nextAttemptAt)}
        </span>
      ),
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      // Nothing to ask for on the internal leaf: it is not issued over ACME, and the service that
      // signs it reissues on its own when the LAN names change or it nears expiry.
      cell: (c) =>
        c.source === 'internal' ? null : (
          <Button
            size="sm"
            variant="secondary"
            loading={renew.isPending && renew.variables === c.host}
            onClick={() => renew.mutate(c.host)}
          >
            <RefreshCw /> Renew now
          </Button>
        ),
    },
  ]

  const renderCard = (c: CertificateInfo) => (
    <div className="space-y-3">
      <div className="flex items-start justify-between gap-3">
        <span className="min-w-0 truncate font-medium text-text">{c.host}</span>
        <Badge tone={CERT_STATE_TONE[c.state]}>{CERT_STATE_LABEL[c.state]}</Badge>
      </div>
      <p className="text-[13px] text-text-2">
        {CERT_SOURCE_LABEL[c.source]} · expires{' '}
        <span title={absoluteTitle(c.notAfter)}>{relativeTime(c.notAfter)}</span> · next attempt{' '}
        <span title={absoluteTitle(c.nextAttemptAt)}>{relativeTime(c.nextAttemptAt)}</span>
      </p>
      {c.lastError && <p className="text-[13px] text-danger">{c.lastError}</p>}
      {c.source !== 'internal' && (
        <div className="flex justify-end border-t border-border pt-3">
          <Button
            size="sm"
            variant="secondary"
            loading={renew.isPending && renew.variables === c.host}
            onClick={() => renew.mutate(c.host)}
          >
            <RefreshCw /> Renew now
          </Button>
        </div>
      )}
    </div>
  )

  return (
    <Card>
      <CardContent>
        <SectionHeader
          title="Certificates"
          description="Issued by Watchtower itself over ACME and renewed at a third of their lifetime. A host has no certificate until its DNS points here and the first order completes — HTTPS fails for it until then."
        />
        {isError ? (
          <Banner
            tone="danger"
            title="Couldn’t load certificates"
            action={
              <Button variant="link" onClick={() => refetch()}>
                Retry
              </Button>
            }
          >
            {(error as Error)?.message ?? 'An unexpected error occurred.'}
          </Banner>
        ) : (
          <DataList
            items={certificates}
            getKey={(c) => c.host}
            columns={columns}
            renderCard={renderCard}
            skeletonRows={isLoading ? 3 : undefined}
            emptyState={
              <p className="text-[13px] text-text-2">
                No certificates yet. One is ordered per TLS route and per realm login host as soon as
                the proxy is enabled.
              </p>
            }
            aria-label="Certificates"
          />
        )}
      </CardContent>
    </Card>
  )
}

/**
 * Watchtower's own certificate authority (ADR-0033), shown only once it exists — which happens the
 * first time a port route needs a LAN certificate. Reading it never mints a root, so an operator with
 * no port routes never sees an invitation to import something nothing uses, and the card is simply
 * absent until then.
 *
 * Its own card rather than a block inside the ACME one: it belongs to the port routes, which every
 * provider serves (ADR-0033 addendum), while the ACME table belongs to the built-in provider alone.
 */
function InternalCaCard() {
  const { data: ca } = useQuery({
    queryKey: ['proxy', 'internal-ca'],
    queryFn: api.proxy.getInternalCa,
  })
  if (!ca?.present) return null

  return (
    <Card>
      <CardContent>
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0 space-y-1">
            <div className="flex items-center gap-2">
              <ShieldCheck className="size-4 text-brand" />
              <span className="font-medium text-text">Internal CA</span>
            </div>
            <p className="font-mono text-[13px] text-text-2">{ca.subject}</p>
            <p className="text-xs text-text-3">
              Root expires <span title={absoluteTitle(ca.notAfter)}>{relativeTime(ca.notAfter)}</span>
              {ca.leafNotAfter && (
                <>
                  {' '}· LAN certificate expires{' '}
                  <span title={absoluteTitle(ca.leafNotAfter)}>{relativeTime(ca.leafNotAfter)}</span>
                </>
              )}
            </p>
            {ca.subjectAltNames.length > 0 && (
              <p className="text-xs text-text-3">
                Valid for <span className="font-mono">{ca.subjectAltNames.join(', ')}</span>
              </p>
            )}
          </div>
          <Button size="sm" variant="secondary" asChild>
            <a href={INTERNAL_CA_DOWNLOAD_URL} download>
              <Download /> Download root
            </a>
          </Button>
        </div>
        <p className="mt-3 text-xs text-text-3">
          Import this into your OS or browser trust store so LAN addresses validate.
        </p>
      </CardContent>
    </Card>
  )
}

/**
 * A route access policy while it is being edited — every field the policy has, whatever the mode, so
 * switching modes back and forth keeps what was chosen for each. {@link toRouteAccess} drops what the chosen
 * mode does not use when it is sent.
 */
interface AccessDraft {
  mode: AccessMode
  identityHeaderMode: IdentityHeaderMode
  bypassPaths: string
  grantedUserIds: number[]
  grantedGroupIds: number[]
  accessRuleIds: number[]
}

/** The draft for an existing route's policy, as `proxy.getAccess` reported it. */
function accessDraftFrom(view: RouteAccess): AccessDraft {
  return {
    mode: view.mode,
    identityHeaderMode: view.identityHeaderMode,
    bypassPaths: view.bypassPaths ?? '',
    grantedUserIds: view.grantedUserIds,
    grantedGroupIds: view.grantedGroupIds,
    accessRuleIds: view.accessRuleIds ?? [],
  }
}

/**
 * The policy as it is sent: only the parts the chosen mode uses. The backend clears each for the modes they
 * don't belong to anyway, but retained text or selections from another mode are not sent either.
 */
function toRouteAccess(draft: AccessDraft): RouteAccess {
  return {
    mode: draft.mode,
    identityHeaderMode: draft.identityHeaderMode,
    bypassPaths: draft.mode === 'Public' || draft.bypassPaths.trim() === '' ? null : draft.bypassPaths,
    grantedUserIds: draft.mode === 'Restricted' ? draft.grantedUserIds : [],
    grantedGroupIds: draft.mode === 'Restricted' ? draft.grantedGroupIds : [],
    // Always an array, never null: a form that shows the attachments knows what they should be, so an
    // untick has to be sent as the empty list that detaches. Null is for clients that do not.
    accessRuleIds: draft.mode === 'Authenticated' ? draft.accessRuleIds : [],
  }
}

/**
 * Who may reach a route — the one access editor, used by the route form both to create a route and
 * to edit one (ADR-0039, decision 5 as amended). One component on purpose: the create
 * form used to carry a smaller copy that could not name grants or rules, which is what made creating a
 * protected route a two-step job that published it under the instance-wide allow-list first.
 */
function AccessFields({
  value,
  onChange,
  realmName,
  users,
  groups,
  accessRules,
  activeEnforcementPoint,
  pickersNote = null,
}: {
  value: AccessDraft
  onChange: (next: AccessDraft) => void
  /**
   * Shown in place of the user, group and rule pickers while they cannot be offered yet — a new route has
   * no realm until its stack is chosen, and an empty roster there would claim the realm has nobody in it.
   */
  pickersNote?: string | null
  /**
   * The realm the candidate lists are scoped to, named in the copy so the shorter lists make sense —
   * or null while the roster has not answered, in which case the copy says the scoping without naming
   * it rather than inventing a placeholder name.
   */
  realmName: string | null
  users: { id: number; userName: string; email: string | null }[]
  groups: { id: number; name: string; memberCount: number }[]
  accessRules: AccessRule[]
  /** Which enforcement point will decide the policy — what makes a rule attachable or not. */
  activeEnforcementPoint: ActiveEnforcementPoint
}) {
  const { mode, identityHeaderMode, bypassPaths, grantedUserIds, grantedGroupIds, accessRuleIds } = value
  const set = (patch: Partial<AccessDraft>) => onChange({ ...value, ...patch })
  const setMode = (next: AccessMode) => set({ mode: next })
  const setIdentityHeaderMode = (next: IdentityHeaderMode) => set({ identityHeaderMode: next })
  const setBypassPaths = (next: string) => set({ bypassPaths: next })
  const toggle = (ids: number[], id: number) => (ids.includes(id) ? ids.filter((x) => x !== id) : [...ids, id])
  const toggleUser = (id: number) => set({ grantedUserIds: toggle(grantedUserIds, id) })
  const toggleGroup = (id: number) => set({ grantedGroupIds: toggle(grantedGroupIds, id) })
  // Appended rather than inserted in roster order: the list's order is the precedence the policies attach in
  // at the edge, so ticking a rule puts it after the ones already chosen.
  const toggleRule = (id: number) => set({ accessRuleIds: toggle(accessRuleIds, id) })

  return (
    <>
      <Field label="Who can access">
        {({ id }) => (
          <Select value={mode} onValueChange={(v) => setMode(v as AccessMode)}>
            <SelectTrigger id={id}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {ACCESS_MODES.map((m) => (
                <SelectItem key={m.value} value={m.value}>
                  {m.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
      </Field>
      <p className="-mt-2 text-xs text-text-3">
        {accessModeDescriptionAt(mode, activeEnforcementPoint)}
      </p>

      {mode === 'Authenticated' && (
        <Field
          label="Access rules"
          hint="Tick the named allow-lists this hostname admits. Tick two to admit both. Leave all unticked to use the instance-wide allow sources from Settings → Reverse proxy, which apply to every protected hostname alike."
        >
          {() =>
            pickersNote ? (
              <p className="text-[13px] text-text-3">{pickersNote}</p>
            ) : accessRules.length === 0 ? (
              <p className="text-[13px] text-text-3">
                No access rules yet. Create one in the <span className="text-text-2">Access rules</span> card on
                the Routes page to admit a different set of people here than on your other hostnames.
              </p>
            ) : (
              <div className="max-h-52 overflow-y-auto rounded-md border border-border">
                {accessRules.map((rule) => {
                  // A rule the active provider cannot honour would be refused on save (ADR-0039 decision 4),
                  // so it is disabled here with the reason rather than offered and then rejected.
                  const attachable = isAttachableAt(rule, activeEnforcementPoint)
                  const checked = accessRuleIds.includes(rule.id)
                  const position = accessRuleIds.indexOf(rule.id)
                  return (
                    <label
                      key={rule.id}
                      className={`flex items-center gap-3 border-b border-border px-3 py-2 last:border-b-0 ${
                        attachable ? 'cursor-pointer hover:bg-surface-2' : 'cursor-not-allowed opacity-60'
                      }`}
                    >
                      <input
                        type="checkbox"
                        className="size-4 accent-brand"
                        checked={checked}
                        disabled={!attachable}
                        onChange={() => toggleRule(rule.id)}
                      />
                      <span className="min-w-0 flex-1">
                        <span className="text-sm text-text">{rule.name}</span>
                        {checked && accessRuleIds.length > 1 && (
                          <span className="ml-2 text-xs text-text-3">#{position + 1}</span>
                        )}
                        <span className="ml-2 text-xs text-text-3">
                          {attachable
                            ? (rule.description ??
                              (rule.clauses.length === 1 ? '1 clause' : `${rule.clauses.length} clauses`))
                            : activeEnforcementPoint === 'CloudflareAccess'
                              ? 'Cloudflare Access cannot enforce every clause in this rule'
                              : 'the built-in proxy cannot enforce every clause in this rule'}
                        </span>
                      </span>
                    </label>
                  )
                })}
              </div>
            )
          }
        </Field>
      )}

      {mode === 'Authenticated' && accessRuleIds.length > 1 && (
        <p className="-mt-2 text-xs text-text-3">
          Ticked rules are attached in the order shown, which is the order the edge evaluates them in. Anyone
          matching any of them gets in.
        </p>
      )}

      {mode === 'Restricted' && (
        <Field
          label="Allowed users"
          hint={
            realmName
              ? `Accounts in the ${realmName} realm — the population this route belongs to. Only they can be granted it.`
              : 'Accounts in the realm this route belongs to. Only they can be granted it.'
          }
        >
          {() =>
            pickersNote ? (
              <p className="text-[13px] text-text-3">{pickersNote}</p>
            ) : users.length === 0 ? (
              <p className="text-[13px] text-text-3">
                {realmName
                  ? `No accounts in the ${realmName} realm yet.`
                  : 'No accounts in this route’s realm yet.'}{' '}
                Add them on the Users page, then grant them here.
              </p>
            ) : (
              <div className="max-h-52 overflow-y-auto rounded-md border border-border">
                {users.map((u) => (
                  <label
                    key={u.id}
                    className="flex cursor-pointer items-center gap-3 border-b border-border px-3 py-2 last:border-b-0 hover:bg-surface-2"
                  >
                    <input
                      type="checkbox"
                      className="size-4 accent-brand"
                      checked={grantedUserIds.includes(u.id)}
                      onChange={() => toggleUser(u.id)}
                    />
                    <span className="min-w-0 flex-1">
                      <span className="text-sm text-text">{u.userName}</span>
                      {u.email && <span className="ml-2 text-xs text-text-3">{u.email}</span>}
                    </span>
                  </label>
                ))}
              </div>
            )
          }
        </Field>
      )}

      {mode === 'Restricted' && (
        <Field
          label="Allowed groups"
          hint="Everyone in a ticked group gets in, evaluated per request — so adding or removing a member takes effect immediately."
        >
          {() =>
            pickersNote ? (
              <p className="text-[13px] text-text-3">{pickersNote}</p>
            ) : groups.length === 0 ? (
              <p className="text-[13px] text-text-3">
                {realmName
                  ? `No groups in the ${realmName} realm yet.`
                  : 'No groups in this route’s realm yet.'}{' '}
                Create one on the Groups page to grant several accounts at once.
              </p>
            ) : (
              <div className="max-h-52 overflow-y-auto rounded-md border border-border">
                {groups.map((g) => (
                  <label
                    key={g.id}
                    className="flex cursor-pointer items-center gap-3 border-b border-border px-3 py-2 last:border-b-0 hover:bg-surface-2"
                  >
                    <input
                      type="checkbox"
                      className="size-4 accent-brand"
                      checked={grantedGroupIds.includes(g.id)}
                      onChange={() => toggleGroup(g.id)}
                    />
                    <span className="min-w-0 flex-1">
                      <span className="text-sm text-text">{g.name}</span>
                      <span className="ml-2 text-xs text-text-3">
                        {g.memberCount === 1 ? '1 member' : `${g.memberCount} members`}
                      </span>
                    </span>
                  </label>
                ))}
              </div>
            )
          }
        </Field>
      )}

      {mode !== 'Public' && (
        <Field label="Identity forwarding">
          {({ id }) => (
            <>
              <Select
                value={identityHeaderMode}
                onValueChange={(v) => setIdentityHeaderMode(v as IdentityHeaderMode)}
              >
                <SelectTrigger id={id}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {IDENTITY_HEADER_MODES.map((m) => (
                    <SelectItem key={m.value} value={m.value}>
                      {m.label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <p className="mt-1.5 text-xs text-text-3">
                Most apps validate the signed JWT (X-Watchtower-Jwt), which always carries the user and
                their groups. Choose a header mode only for apps that read plaintext username and group
                headers instead.
              </p>
            </>
          )}
        </Field>
      )}

      {mode !== 'Public' && (
        <Field
          label="Bypass paths"
          hint="Paths exempt from access control, e.g. /api/webhooks/*. One per line."
        >
          {({ id, describedBy }) => (
            <Textarea
              id={id}
              aria-describedby={describedBy}
              mono
              value={bypassPaths}
              onChange={(e) => setBypassPaths(e.target.value)}
              placeholder={'/api/webhooks/\n/healthz'}
              spellCheck={false}
            />
          )}
        </Field>
      )}
    </>
  )
}
