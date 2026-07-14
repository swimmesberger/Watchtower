import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ExternalLink, Globe, Plus, Trash2, X } from 'lucide-react'
import { api } from '@/lib/api'
import type { CreateRouteRequest, Route, RouteStatus } from '@/lib/types'
import { timeAgo } from '@/lib/format'
import { Badge, type BadgeTone } from '@/components/ui/badge'
import { Banner } from '@/components/ui/banner'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { DataList, type DataListColumn } from '@/components/ui/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, type InputProps } from '@/components/ui/input'
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

const emptyForm = {
  stackId: '',
  domain: '',
  serviceName: '',
  containerPort: '',
  tlsEnabled: true,
  // True once the user opts out of the discovered-value dropdown to type a custom value.
  serviceManual: false,
  portManual: false,
}

const MANUAL = '__manual__'

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
  const [showForm, setShowForm] = useState(false)
  const [form, setForm] = useState({ ...emptyForm })
  const [pendingDelete, setPendingDelete] = useState<Route | null>(null)

  const { data: status } = useQuery({ queryKey: ['proxy-status'], queryFn: api.proxy.getStatus })

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

  const { data: stacks = [] } = useQuery({ queryKey: ['stacks'], queryFn: api.stacks.list })

  const selectedStack = stacks.find((s) => String(s.id) === form.stackId)
  const stackProject = selectedStack?.composeProjectName

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
      toast.success(`Route ${route.domain} created.`)
      qc.invalidateQueries({ queryKey: ['routes'] })
      setForm({ ...emptyForm })
      dns.reset()
      setShowForm(false)
    },
    onError: (err: Error) => toast.error(err.message),
  })

  const remove = useMutation({
    mutationFn: (route: Route) => api.proxy.deleteRoute(route.id),
    onSuccess: (_data, route) => {
      toast.success(`Deleted ${route.domain}.`)
      qc.invalidateQueries({ queryKey: ['routes'] })
    },
    onError: (err: Error, route) => toast.error(`Failed to delete ${route.domain}: ${err.message}`),
    onSettled: () => setPendingDelete(null),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    const stackId = Number(form.stackId)
    const containerPort = Number(form.containerPort)
    if (!stackId) return toast.error('Choose a stack.')
    if (!form.domain.trim()) return toast.error('Enter a domain.')
    if (!form.serviceName.trim()) return toast.error('Enter a service name.')
    if (!containerPort || containerPort < 1 || containerPort > 65535)
      return toast.error('Enter a valid container port (1–65535).')
    create.mutate({
      stackId,
      domain: form.domain.trim(),
      serviceName: form.serviceName.trim(),
      containerPort,
      tlsEnabled: form.tlsEnabled,
      isPrimary: false,
    })
  }

  const columns: DataListColumn<Route>[] = [
    {
      key: 'domain',
      header: 'Domain',
      cell: (r) => (
        <a
          href={`${r.tlsEnabled ? 'https' : 'http'}://${r.domain}`}
          target="_blank"
          rel="noreferrer"
          className="inline-flex items-center gap-1.5 font-medium text-text hover:text-brand"
        >
          {r.domain}
          <ExternalLink className="size-3.5 text-text-3" />
        </a>
      ),
    },
    {
      key: 'stack',
      header: 'Stack',
      cell: (r) => <span className="text-[13px] text-text-2">{r.stackName ?? `#${r.stackId}`}</span>,
    },
    {
      key: 'target',
      header: 'Target',
      cell: (r) => (
        <span className="font-mono text-[13px] text-text-2">
          {r.serviceName}:{r.containerPort}
        </span>
      ),
    },
    {
      key: 'tls',
      header: 'TLS',
      cell: (r) => (
        <Badge tone={r.tlsEnabled ? 'ok' : 'neutral'}>{r.tlsEnabled ? 'HTTPS' : 'HTTP'}</Badge>
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
        <Tooltip label="Delete route">
          <Button
            size="icon-sm"
            variant="ghost"
            aria-label={`Delete ${r.domain}`}
            onClick={() => setPendingDelete(r)}
            className="text-text-2 hover:text-danger"
          >
            <Trash2 />
          </Button>
        </Tooltip>
      ),
    },
  ]

  const renderCard = (r: Route) => (
    <div className="space-y-3">
      <div className="flex items-start justify-between gap-3">
        <a
          href={`${r.tlsEnabled ? 'https' : 'http'}://${r.domain}`}
          target="_blank"
          rel="noreferrer"
          className="inline-flex items-center gap-1.5 font-medium text-text hover:text-brand"
        >
          {r.domain}
          <ExternalLink className="size-3.5 text-text-3" />
        </a>
        <Badge tone={STATUS_TONE[r.status]}>{STATUS_LABEL[r.status]}</Badge>
      </div>
      <p className="text-[13px] text-text-2">
        {r.stackName ?? `#${r.stackId}`} ·{' '}
        <span className="font-mono">
          {r.serviceName}:{r.containerPort}
        </span>{' '}
        · {r.tlsEnabled ? 'HTTPS' : 'HTTP'}
      </p>
      <div className="flex items-center justify-between border-t border-border pt-3">
        <span className="text-xs text-text-3">created {timeAgo(r.createdAt)}</span>
        <Button
          size="icon-sm"
          variant="ghost"
          aria-label={`Delete ${r.domain}`}
          onClick={() => setPendingDelete(r)}
          className="text-text-2 hover:text-danger"
        >
          <Trash2 />
        </Button>
      </div>
    </div>
  )

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-4 md:p-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-[24px] font-semibold leading-tight tracking-[-0.02em]">Routes</h1>
          {status && (
            <Badge tone={status.enabled ? (status.caddyRunning ? 'ok' : 'warn') : 'neutral'}>
              {status.enabled ? (status.caddyRunning ? 'Proxy running' : 'Proxy starting…') : 'Proxy disabled'}
            </Badge>
          )}
        </div>
        <Button variant="primary" onClick={() => setShowForm((v) => !v)} disabled={stacks.length === 0}>
          {showForm ? <X /> : <Plus />} {showForm ? 'Cancel' : 'New route'}
        </Button>
      </div>

      {status && !status.enabled && (
        <Banner tone="warn" title="Reverse proxy is disabled">
          Routes are saved but not served until the proxy is enabled. Set{' '}
          <code className="font-mono">WATCHTOWER__PROXY__ENABLED=true</code> (and optionally{' '}
          <code className="font-mono">WATCHTOWER__PROXY__ADMINEMAIL</code>) and restart Watchtower. Host
          ports 80 and 443 must be free.
        </Banner>
      )}

      {showForm && (
        <Card>
          <CardContent className="pt-5">
            <SectionHeader
              title="New route"
              description="Point a domain at a service inside a stack. HTTPS is provisioned automatically."
            />
            <form onSubmit={submit} className="space-y-4">
              <Field label="Domain" required hint="e.g. app.example.com — point its DNS at this host">
                {({ id, describedBy }) => (
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
                      disabled={!form.domain.trim()}
                      onClick={() => dns.mutate(form.domain.trim())}
                      className="shrink-0"
                    >
                      Check DNS
                    </Button>
                  </div>
                )}
              </Field>

              {dns.data && (
                <p className={`text-[13px] ${dns.data.resolves ? 'text-ok' : 'text-warn'}`}>
                  {dns.data.resolves
                    ? `Resolves to ${dns.data.addresses.join(', ')}. Make sure that points at this host.`
                    : 'Does not resolve yet — add a DNS record pointing this domain at your server.'}
                </p>
              )}

              <div className="grid gap-4 md:grid-cols-2">
                <Field label="Stack" required>
                  {({ id, describedBy }) => (
                    <Select
                      value={form.stackId}
                      onValueChange={(v) =>
                        // Switching stacks invalidates the service/port chosen for the old one.
                        setForm((f) => ({
                          ...f,
                          stackId: v,
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

              <div className="flex justify-end gap-3">
                <Button type="button" variant="secondary" onClick={() => setShowForm(false)}>
                  Cancel
                </Button>
                <Button type="submit" loading={create.isPending}>
                  Create route
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

      {!isError && (
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
                  ? 'Create a stack first, then add a route to expose one of its services.'
                  : 'Add a route to expose a service on a domain with automatic HTTPS.'
              }
              action={
                stacks.length > 0 ? (
                  <Button variant="primary" onClick={() => setShowForm(true)}>
                    <Plus /> New route
                  </Button>
                ) : undefined
              }
            />
          }
          aria-label="Routes"
        />
      )}

      <ConfirmDialog
        open={pendingDelete != null}
        onOpenChange={(open) => {
          if (!open && !remove.isPending) setPendingDelete(null)
        }}
        title={pendingDelete ? `Delete ${pendingDelete.domain}?` : 'Delete route?'}
        description="The proxy will stop serving this domain. The target container keeps running."
        confirmLabel="Delete"
        tone="danger"
        loading={remove.isPending}
        onConfirm={() => {
          if (pendingDelete) remove.mutate(pendingDelete)
        }}
      />
    </div>
  )
}
