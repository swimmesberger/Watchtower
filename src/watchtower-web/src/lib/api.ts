// Thin ergonomic wrapper over the generated JSON-RPC client. Route components keep calling
// `api.stacks.list()` etc.; each method issues a typed JSON-RPC call and unwraps the envelope.
// Nullable params are built explicitly (`?? null`) because the generated param types require
// every key to be present.
import { rpc } from './rpc-client'
import { apiBase } from './config'
import type {
  AdoptStackResult,
  HostRegistry,
  CloudflareForeignRoute,
  ActiveDeployment,
  AuditQuery,
  AuditFacets,
  AuditEventPage,
  AuthConfig,
  AutomationConfig,
  BackupBundle,
  BackupConfig,
  BackupEvent,
  BackupEventKind,
  BackupRemoteFile,
  InstanceRestoreStatus,
  RecoveryChecklist,
  RecoveryStack,
  RestoreValidation,
  BackupRunAccepted,
  BackupPlanPreview,
  BackupQuiesceMode,
  BackupServiceOverride,
  BackupStackConfig,
  BackupTemplatePolicy,
  ProductBackups,
  UpdateBackupConfigRequest,
  Container,
  ContainerEnvVar,
  ContainerMetrics,
  AddTenantRequest,
  CiLink,
  CiRepo,
  CertificateInfo,
  CreateCredentialRequest,
  CreateRealmRequest,
  CreateRegistryRequest,
  CreateRouteRequest,
  CreateProductRequest,
  CreateStackRequest,
  CreateTemplateRequest,
  Credential,
  DeployAccepted,
  DeployEvent,
  ReleaseRollout,
  RetryFailedRolloutResult,
  SetTenantsReleaseResult,
  DeployReleaseResult,
  DnsCheckResult,
  DockerConfigStatus,
  Group,
  HostMetrics,
  InternalCaInfo,
  LanNameCandidate,
  MetricsConfig,
  MetricsRange,
  NetworkInfo,
  NetworkPortsResult,
  PortBindingsApplied,
  PortBindingsStatus,
  Product,
  ProductDetail,
  CreateReleaseRequest,
  ReleaseDetail,
  ReleasePage,
  ReleaseTokenRotation,
  ReleaseWebhookState,
  PruneOrphansResult,
  ProxyConfig,
  ProxyStatus,
  Realm,
  Registry,
  Route,
  RouteAccess,
  RouteAccessView,
  SelfUpdateStatus,
  SetStackReleaseResult,
  Stack,
  StackTemplate,
  Tenant,
  TemplateEnvVar,
  TemplateGrant,
  UpdateTemplateRequest,
  HostGpus,
  StackDeviceMappingInput,
  StackDevices,
  StackEnvVar,
  StackEnvVarInput,
  StackMetricsResult,
  UpdateAuthConfigRequest,
  UpdateAutomationRequest,
  UpdateCredentialRequest,
  UpdateMetricsConfigRequest,
  UpdateProductRequest,
  UpdateProxyConfigRequest,
  UpdateRealmRequest,
  UpdateRegistryRequest,
  UpdateRouteRequest,
  UpdateSelfConfigRequest,
  UpdateStackRequest,
  UpdateUserRequest,
  User,
  CreateUserRequest,
  VolumeInfo,
  VolumeSize,
} from './types'

/**
 * Where a staged full backup bundle is fetched from (ADR-0027). A plain link rather than an RPC call:
 * it streams a tar of arbitrary size, and the browser's own download handling is what should own it.
 * Admin-only, and authenticated by the same session cookie every other request carries.
 */
export const BUNDLE_DOWNLOAD_URL = `${apiBase}/api/instance/bundle`

/**
 * Where the internal CA's root certificate is fetched from (ADR-0033), for importing into an OS or
 * browser trust store. A plain link for the same reason the bundle is one: the browser's own download
 * handling should own the file, and the session cookie authenticates it like every other request.
 */
export const INTERNAL_CA_DOWNLOAD_URL = `${apiBase}/api/proxy/internal-ca.crt`

/** Where an operator's bundle is uploaded for a restore. Admin-only; see {@link uploadRestoreBundle}. */
const BUNDLE_UPLOAD_URL = `${apiBase}/api/instance/restore/bundle`

/**
 * Uploads a bundle and returns this instance's verdict on restoring it (ADR-0027). Nothing is replaced
 * here — the upload is staged, and `backups.startInstanceRestore` is what acts on it.
 *
 * A plain fetch rather than an RPC call: the body is a tar of arbitrary size, streamed straight to disk
 * on the other end.
 */
export async function uploadRestoreBundle(
  file: File,
  signal?: AbortSignal,
): Promise<RestoreValidation> {
  const response = await fetch(BUNDLE_UPLOAD_URL, {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/x-tar' },
    body: file,
    signal,
  })
  if (!response.ok) {
    // The endpoint answers a rejected upload with a problem document whose `detail` says why.
    const problem = await response.json().catch(() => null)
    throw new Error(
      problem?.detail ?? problem?.title ?? `The upload failed (HTTP ${response.status}).`,
    )
  }
  return (await response.json()) as RestoreValidation
}

export const api = {
  registries: {
    list: async () => (await rpc('registries.list', {})).registries as Registry[],
    listWithHost: async () => {
      const result = await rpc('registries.list', {})
      return {
        registries: result.registries as Registry[],
        hostRegistries: result.hostRegistries as HostRegistry[],
      }
    },
    create: async (data: CreateRegistryRequest) =>
      (await rpc('registries.create', {
        name: data.name,
        url: data.url,
        credentialId: data.credentialId ?? null,
      })).registry as Registry,
    update: async (id: number, data: UpdateRegistryRequest) =>
      (await rpc('registries.update', {
        id,
        name: data.name,
        url: data.url,
        credentialId: data.credentialId ?? null,
      })).registry as Registry,
    delete: async (id: number) => {
      await rpc('registries.delete', { id })
    },
    test: async (id: number) => (await rpc('registries.test', { id })).message,
  },

  credentials: {
    list: async () => (await rpc('credentials.list', {})).credentials as Credential[],
    create: async (data: CreateCredentialRequest) =>
      (await rpc('credentials.create', {
        name: data.name,
        username: data.username,
        token: data.token,
      })).credential as Credential,
    update: async (id: number, data: UpdateCredentialRequest) =>
      (await rpc('credentials.update', {
        id,
        name: data.name,
        username: data.username,
        token: data.token ?? null,
      })).credential as Credential,
    delete: async (id: number) => {
      await rpc('credentials.delete', { id })
    },
  },

  products: {
    list: async () => (await rpc('products.list', {})).products as Product[],
    // The whole envelope: the rosters are what the detail page is for, so splitting them into a
    // second call would only make the page fetch twice for one screen.
    get: async (id: number) => (await rpc('products.get', { id })) as ProductDetail,
    create: async (data: CreateProductRequest) =>
      (await rpc('products.create', {
        name: data.name,
        repositoryUrl: data.repositoryUrl,
        composeFilePath: data.composeFilePath,
        defaultBranch: data.defaultBranch,
        description: data.description ?? null,
        credentialId: data.credentialId ?? null,
      })).product as Product,
    update: async (id: number, data: UpdateProductRequest) =>
      (await rpc('products.update', {
        id,
        name: data.name,
        repositoryUrl: data.repositoryUrl,
        composeFilePath: data.composeFilePath,
        defaultBranch: data.defaultBranch,
        description: data.description ?? null,
        credentialId: data.credentialId ?? null,
        // Null means "leave it alone", which is what every caller but the mode control sends.
        releaseMode: data.releaseMode ?? null,
        retainReleases: data.retainReleases ?? null,
      })).product as Product,
    delete: async (id: number) => {
      await rpc('products.delete', { id })
    },

    // ── releases (ADR-0026 stage 3) ──────────────────────────────────────
    // Keyset paging on the id: `before` is the last id of the page you have, not an offset, so a
    // release published while somebody pages cannot shift the window.
    listReleases: async (productId: number, before?: number, limit = 20) =>
      (await rpc('products.listReleases', {
        productId,
        before: before ?? null,
        limit,
      })) as ReleasePage,
    getRelease: async (releaseId: number) =>
      (await rpc('products.getRelease', { releaseId })).release as ReleaseDetail,
    createRelease: async (productId: number, data: CreateReleaseRequest) =>
      (await rpc('products.createRelease', {
        productId,
        version: data.version,
        images: data.images,
        commitSha: data.commitSha ?? null,
        notes: data.notes ?? null,
      })).release as ReleaseDetail,
    deleteRelease: async (releaseId: number) => {
      await rpc('products.deleteRelease', { releaseId })
    },
    rotateReleaseToken: async (productId: number) =>
      (await rpc('products.rotateReleaseToken', { productId })) as ReleaseTokenRotation,
    setReleaseWebhook: async (productId: number, enabled: boolean) =>
      (await rpc('products.setReleaseWebhook', { productId, enabled })) as ReleaseWebhookState,

    /**
     * Rolls the newest release out to every latest-tracking, running stack of the product.
     *
     * `releaseId` is the staleness guard, not the target: pass the id the dialog displayed and the call
     * is refused with `409` when a newer release landed while the dialog was open. The deploys
     * themselves resolve `pin ?? newest` at execution time (invariant 3), so what they run is always the
     * true newest.
     */
    deployRelease: async (productId: number, releaseId?: number | null) =>
      (await rpc('products.deployRelease', {
        productId,
        releaseId: releaseId ?? null,
      })) as DeployReleaseResult,

    /**
     * What one release actually reached: a row per stack of the product, and the counts above them.
     *
     * The rows with a deploy event are history; the skipped ones describe the stack as it is **now**,
     * because the fan-out deliberately records nothing per stack it did not target.
     */
    getReleaseRollout: async (releaseId: number) =>
      (await rpc('products.getReleaseRollout', { releaseId })).rollout as ReleaseRollout,

    /**
     * Re-enqueues the stacks whose newest deploy of this release failed, and only those. Stopped
     * stacks and ones now pinned elsewhere are reported as `skipped` rather than deployed — the
     * second would deploy its pin instead of this release, which is not what the button says.
     */
    retryFailedRollout: async (releaseId: number) =>
      (await rpc('products.retryFailedRollout', { releaseId })) as RetryFailedRolloutResult,
  },

  stacks: {
    list: async () => (await rpc('stacks.list', {})).stacks as Stack[],
    get: async (id: number) => (await rpc('stacks.get', { id })).stack as Stack,
    create: async (data: CreateStackRequest) =>
      (await rpc('stacks.create', {
        name: data.name,
        productId: data.productId ?? null,
        repositoryUrl: data.repositoryUrl,
        composeFilePath: data.composeFilePath,
        branch: data.branch,
        composeProjectName: data.composeProjectName ?? null,
        credentialId: data.credentialId ?? null,
        webhookToken: data.webhookToken ?? null,
        webhookEnabled: data.webhookEnabled ?? false,
        autoDeployMode: data.autoDeployMode ?? 'off',
        autoDeployTime: data.autoDeployTime ?? null,
        envVars: data.envVars ?? null,
      })).stack as Stack,
    update: async (id: number, data: UpdateStackRequest) =>
      (await rpc('stacks.update', {
        id,
        name: data.name,
        repositoryUrl: data.repositoryUrl,
        composeFilePath: data.composeFilePath,
        branch: data.branch,
        composeProjectName: data.composeProjectName ?? null,
        credentialId: data.credentialId ?? null,
        webhookToken: data.webhookToken ?? null,
        webhookEnabled: data.webhookEnabled ?? false,
        autoDeployMode: data.autoDeployMode ?? 'off',
        autoDeployTime: data.autoDeployTime ?? null,
        envVars: data.envVars ?? null,
      })).stack as Stack,
    delete: async (id: number) => {
      await rpc('stacks.delete', { id })
    },
    deploy: async (id: number) => (await rpc('stacks.deploy', { id })).deploy as DeployAccepted,
    stop: async (id: number) => (await rpc('stacks.stop', { id })).stack as Stack,
    start: async (id: number) =>
      (await rpc('stacks.start', { id })) as { stack: Stack; started: boolean },
    events: async (id: number) => (await rpc('stacks.events', { stackId: id })).events as DeployEvent[],
    getEnv: async (id: number) => (await rpc('stacks.getEnv', { stackId: id })).envVars as StackEnvVar[],
    setEnv: async (id: number, vars: StackEnvVarInput[]) =>
      (await rpc('stacks.setEnv', { stackId: id, vars })).envVars as StackEnvVar[],
    getDevices: async (id: number) =>
      (await rpc('stacks.getDevices', { stackId: id })) as StackDevices,
    setDevices: async (id: number, devices: StackDeviceMappingInput[], gpuServices: string[]) =>
      (await rpc('stacks.setDevices', {
        stackId: id,
        devices: devices.map((d) => ({
          service: d.service,
          hostPath: d.hostPath,
          containerPath: d.containerPath ?? null,
          permissions: d.permissions ?? null,
        })),
        gpuServices,
      })) as StackDevices,
    hostGpus: async () => (await rpc('stacks.hostGpus', {})) as HostGpus,
    services: async (id: number) =>
      (await rpc('stacks.services', { stackId: id })).services as string[],

    checkUpdates: async (id: number) => (await rpc('stacks.checkUpdates', { id })).stack as Stack,

    /**
     * Pins this stack to one release, or clears the pin so it tracks latest again.
     *
     * `deploy: false` is the Version dialog's **Save**, `true` its **Save & deploy**. The images are
     * pre-flighted server-side, so a release whose digests are gone comes back as a `409` naming the
     * reference and nothing is written — surface that message verbatim.
     */
    setRelease: async (
      id: number, releaseId: number | null, deploy = true, backupFirst = false) =>
      (await rpc('stacks.setRelease', {
        stackId: id,
        releaseId,
        deploy,
        // Back up first and deploy only on success: the response then carries a backupEventId and
        // `deployed: false`, because the deploy is the chain's to enqueue.
        backupFirst,
      })) as SetStackReleaseResult,
  },

  containers: {
    list: async () => (await rpc('containers.list', {})).containers as Container[],
    env: async (id: string) => (await rpc('containers.env', { id })).envVars as ContainerEnvVar[],
    restart: async (id: string) => {
      await rpc('containers.restart', { id })
    },
    stop: async (id: string) => {
      await rpc('containers.stop', { id })
    },
    remove: async (id: string) => {
      await rpc('containers.remove', { id })
    },
  },

  deployments: {
    active: async () => (await rpc('deployments.active', {})).deployments as ActiveDeployment[],
  },

  ci: {
    getProductCi: async (productId: number) =>
      (await rpc('ci.getProductCi', { productId })).ci as CiLink,
    enableForProduct: async (productId: number, credentialId?: number | null) =>
      (await rpc('ci.enableForProduct', { productId, credentialId: credentialId ?? null }))
        .repo as CiRepo,
    updateRepo: async (repo: {
      id: number
      enabled: boolean
      maxConcurrentRunners: number
      credentialId: number
      runnerImage?: string | null
      extraLabels?: string | null
      allowDockerSocket: boolean
      syncRegistryUrl?: string | null
    }) =>
      (
        await rpc('ci.updateRepo', {
          id: repo.id,
          enabled: repo.enabled,
          maxConcurrentRunners: repo.maxConcurrentRunners,
          credentialId: repo.credentialId,
          runnerImage: repo.runnerImage ?? null,
          extraLabels: repo.extraLabels ?? null,
          allowDockerSocket: repo.allowDockerSocket,
          syncRegistryUrl: repo.syncRegistryUrl ?? null,
        })
      ).repo as CiRepo,
    /**
     * Turns the release-secret sync on or off for one product. Answers the whole CI link, so the tab
     * re-renders from one shape rather than patching a toggle and re-fetching the rest.
     */
    setReleaseSecretsSync: async (productId: number, enabled: boolean) =>
      (await rpc('ci.setReleaseSecretsSync', { productId, enabled })).ci as CiLink,
    /**
     * Recycles one runner container (deregister at GitHub, remove, respawn fresh). `busy` back
     * means the runner is mid-job and was kept — retry with force to kill it, failing that job.
     */
    recycleRunner: (repoId: number, containerId: string, force = false) =>
      rpc('ci.recycleRunner', { repoId, containerId, force }),
    /** Recycles the repo's whole runner pool; busy runners are kept unless forced. */
    recycleRunners: (repoId: number, force = false) => rpc('ci.recycleRunners', { repoId, force }),
  },

  volumes: {
    list: async (project?: string | null) =>
      (await rpc('volumes.list', { project: project ?? null })).volumes as VolumeInfo[],
    sizes: async (project?: string | null) =>
      (await rpc('volumes.sizes', { project: project ?? null })).sizes as VolumeSize[],
    recreate: async (stackId: number, volumeNames: string[]) =>
      (await rpc('volumes.recreate', { stackId, volumeNames })).deploy as DeployAccepted,
    remove: async (name: string) => (await rpc('volumes.remove', { name })).removed,
    pruneOrphans: async () => (await rpc('volumes.pruneOrphans', {})) as PruneOrphansResult,
  },

  networks: {
    list: async (project?: string | null) =>
      (await rpc('networks.list', { project: project ?? null })).networks as NetworkInfo[],
    ports: async (project?: string | null) =>
      (await rpc('networks.ports', { project: project ?? null })) as NetworkPortsResult,
  },

  proxy: {
    listRoutes: async () => (await rpc('proxy.listRoutes', {})).routes as Route[],
    getRoute: async (id: number) => (await rpc('proxy.getRoute', { id })).route as Route,
    createRoute: async (data: CreateRouteRequest) =>
      (await rpc('proxy.createRoute', {
        stackId: data.stackId,
        domain: data.domain,
        serviceName: data.serviceName,
        containerPort: data.containerPort,
        tlsEnabled: data.tlsEnabled,
        isPrimary: data.isPrimary,
        kind: data.kind ?? null,
        target: data.target ?? null,
        realmId: data.realmId ?? null,
        makeLoginRoute: data.makeLoginRoute ?? null,
        binding: data.binding ?? null,
        listenPort: data.listenPort ?? null,
        accessMode: data.accessMode ?? null,
        bypassPaths: data.bypassPaths ?? null,
      })).route as Route,
    updateRoute: async (id: number, data: UpdateRouteRequest) =>
      (await rpc('proxy.updateRoute', {
        id,
        domain: data.domain,
        serviceName: data.serviceName,
        containerPort: data.containerPort,
        tlsEnabled: data.tlsEnabled,
        isPrimary: data.isPrimary,
        kind: data.kind ?? null,
        makeLoginRoute: data.makeLoginRoute ?? null,
        binding: data.binding ?? null,
        listenPort: data.listenPort ?? null,
      })).route as Route,
    // Returns the server's response rather than swallowing it: deleting a realm's login host succeeds
    // and carries a `warning` the caller has to show (ADR-0023).
    deleteRoute: async (id: number, removeFromProvider = false) =>
      (await rpc('proxy.deleteRoute', { id, removeFromProvider })) as { id: number; warning?: string | null },
    checkDns: async (domain: string) =>
      (await rpc('proxy.checkDns', { domain })) as DnsCheckResult,
    listCertificates: async () =>
      (await rpc('proxy.listCertificates', {})).certificates as CertificateInfo[],
    renewCertificate: async (host: string) =>
      (await rpc('proxy.renewCertificate', { host })).certificate as CertificateInfo,
    // Read-only in the strong sense: asking never mints a root. `present: false` means nothing has
    // needed a LAN certificate yet, which is why the Routes page shows the block only once it is there.
    getInternalCa: async () => (await rpc('proxy.getInternalCa', {})).ca as InternalCaInfo,
    // The LAN names this deployment looks like it answers on (the LAN names setting of ADR-0033
    // decision 6). Advisory and read-only: the server never writes the setting. `hint` is the address
    // the browser reached this page with — the one thing this side knows and the server cannot find out
    // for itself — and it comes back as a candidate like any other, held to the same rules.
    suggestLanNames: async (hint: string | null) =>
      (await rpc('proxy.suggestLanNames', { hint })).candidates as LanNameCandidate[],
    // Whether each port route's host port is actually published on Watchtower's container (ADR-0033).
    // Its own call rather than part of getStatus: answering it inspects the Docker daemon, and the
    // status badge is polled from every page.
    getPortBindings: async () => (await rpc('proxy.getPortBindings', {})) as PortBindingsStatus,
    // Recreates Watchtower's own container to publish the pending ports. Answers before the restart
    // lands — the coordinator waits three seconds precisely so this response gets through.
    applyPortBindings: async () => (await rpc('proxy.applyPortBindings', {})) as PortBindingsApplied,
    getStatus: async () => (await rpc('proxy.getStatus', {})) as ProxyStatus,
    listCloudflareForeignRoutes: async () =>
      (await rpc('proxy.listCloudflareForeignRoutes', {})) as {
        routes: CloudflareForeignRoute[]
        warning?: string | null
      },
    getConfig: async () => (await rpc('proxy.getConfig', {})).config as ProxyConfig,
    updateConfig: async (data: UpdateProxyConfigRequest) =>
      (await rpc('proxy.updateConfig', {
        enabled: data.enabled,
        provider: data.provider,
        adminEmail: data.adminEmail ?? null,
        caddyImage: data.caddyImage,
        yarpHttpPort: data.yarpHttpPort ?? null,
        yarpHttpsPort: data.yarpHttpsPort ?? null,
        yarpAcmeDirectoryUrl: data.yarpAcmeDirectoryUrl ?? null,
        yarpAcmeCaBundlePath: data.yarpAcmeCaBundlePath ?? null,
        yarpAcmeEabKeyId: data.yarpAcmeEabKeyId ?? null,
        yarpAcmeEabHmacKey: data.yarpAcmeEabHmacKey ?? null,
        yarpRedirectHttpToHttps: data.yarpRedirectHttpToHttps ?? null,
        portRoutesLanNames: data.portRoutesLanNames ?? null,
        cloudflareAccountId: data.cloudflareAccountId ?? null,
        cloudflareZoneId: data.cloudflareZoneId ?? null,
        cloudflareApiToken: data.cloudflareApiToken ?? null,
        cloudflareTunnelName: data.cloudflareTunnelName ?? null,
        cloudflareTeamDomain: data.cloudflareTeamDomain ?? null,
        cloudflareManaged: data.cloudflareManaged ?? null,
        cloudflaredImage: data.cloudflaredImage ?? null,
        cloudflaredContainerName: data.cloudflaredContainerName ?? null,
        cloudflareAccessAllowedEmails: data.cloudflareAccessAllowedEmails ?? null,
        cloudflareAccessAllowedEmailDomains: data.cloudflareAccessAllowedEmailDomains ?? null,
        cloudflareAccessGroupIds: data.cloudflareAccessGroupIds ?? null,
        cloudflareAccessReusablePolicyIds: data.cloudflareAccessReusablePolicyIds ?? null,
        defaultAccessMode: data.defaultAccessMode ?? null,
      })).config as ProxyConfig,
    getAccess: async (routeId: number) =>
      (await rpc('proxy.getAccess', { routeId })) as RouteAccessView,
    setAccess: async (routeId: number, data: RouteAccess) =>
      (await rpc('proxy.setAccess', {
        routeId,
        mode: data.mode,
        identityHeaderMode: data.identityHeaderMode,
        bypassPaths: data.bypassPaths ?? null,
        grantedUserIds: data.grantedUserIds,
        grantedGroupIds: data.grantedGroupIds,
      })) as RouteAccess,
  },

  backups: {
    getConfig: async () => (await rpc('backups.getConfig', {})).config as BackupConfig,
    updateConfig: async (data: UpdateBackupConfigRequest) =>
      (await rpc('backups.updateConfig', {
        enabled: data.enabled,
        cron: data.cron,
        instanceName: data.instanceName ?? null,
        retentionDays: data.retentionDays,
        retentionMaxCount: data.retentionMaxCount,
        helperImage: data.helperImage,
        provider: data.provider,
        encryptionPassphrase: data.encryptionPassphrase ?? null,
        sftpHost: data.sftpHost ?? null,
        sftpPort: data.sftpPort ?? null,
        sftpUsername: data.sftpUsername ?? null,
        sftpPassword: data.sftpPassword ?? null,
        sftpPrivateKey: data.sftpPrivateKey ?? null,
        sftpPrivateKeyPassphrase: data.sftpPrivateKeyPassphrase ?? null,
        sftpBasePath: data.sftpBasePath ?? null,
        localBasePath: data.localBasePath ?? null,
        includeSelf: data.includeSelf ?? null,
        selfPostgresContainer: data.selfPostgresContainer ?? null,
      })).config as BackupConfig,
    testStorage: async () => (await rpc('backups.testStorage', {})).description as string,
    events: async (stackId?: number, limit?: number, productId?: number, kind?: BackupEventKind) =>
      (await rpc('backups.events', {
        stackId: stackId ?? null,
        limit: limit ?? 50,
        // The fleet history: every deployment of one product. Additive — omitting it is the old call.
        productId: productId ?? null,
        // 'instance' for Watchtower's own runs, 'stack' for the rest; omitted returns both (ADR-0027).
        kind: kind ?? null,
      })).events as BackupEvent[],
    run: async (stackId: number) => (await rpc('backups.run', { stackId })).backup as BackupRunAccepted,

    /** Backs up Watchtower's own database (ADR-0027). Admin-only; needs an encryption passphrase. */
    runInstance: async () => (await rpc('backups.runInstance', {})).backup as BackupRunAccepted,
    /** The instance's own archives present on the storage, newest first. Admin-only. */
    listInstance: async () =>
      (await rpc('backups.listInstance', {})) as { files: BackupRemoteFile[]; directory: string },

    /**
     * Starts building a full backup bundle (ADR-0027): a fresh instance dump plus every stack's newest
     * archive, staged for download at {@link BUNDLE_DOWNLOAD_URL}. Admin-only, and slow — poll
     * {@link getBundleStatus}. Returns the tracking event.
     */
    exportBundle: async () =>
      (await rpc('backups.exportBundle', {})).export as BackupRunAccepted,
    /** The staged bundle, or null when there is none. Admin-only. */
    getBundleStatus: async () =>
      (await rpc('backups.getBundleStatus', {})).bundle as BackupBundle | null,

    // ── Restoring this instance from a bundle (ADR-0027) ───────────────────
    /** Whether this instance looks fresh, what bundle is staged, and how the last restore ended. */
    getRestoreStatus: async () =>
      (await rpc('backups.getRestoreStatus', {})) as unknown as InstanceRestoreStatus,
    /**
     * Replaces this instance's database with the uploaded bundle's. Returns once the coordinator has
     * been started — Watchtower stops answering a few seconds later and comes back on the restored
     * database, where the caller's session no longer exists.
     */
    startInstanceRestore: async () =>
      (await rpc('backups.startInstanceRestore', {})).sourceInstance as string,

    /** The post-restore checklist, or null when there is nothing to recover. */
    getRecoveryChecklist: async () =>
      (await rpc('backups.getRecoveryChecklist', {})).checklist as RecoveryChecklist | null,
    /** Deploys one stack from git, then restores its newest archive. Runs to completion. */
    reviveStack: async (stackId: number) =>
      (await rpc('backups.reviveStack', { stackId })).stack as RecoveryStack,
    /** The same for every stack still pending or failed, one after another. */
    reviveAll: async () =>
      (await rpc('backups.reviveAll', {})) as unknown as {
        revived: number
        checklist: RecoveryChecklist | null
      },
    /** Marks one stack as handled outside Watchtower, so "revive all" leaves it alone. */
    skipRecoveryStack: async (stackId: number) =>
      (await rpc('backups.skipRecoveryStack', { stackId })).stack as RecoveryStack,
    /** Puts the checklist away. What happened stays in the audit trail. */
    dismissRecovery: async () => {
      await rpc('backups.dismissRecovery', {})
    },
    listRemote: async (stackId: number) =>
      (await rpc('backups.listRemote', { stackId })).files as BackupRemoteFile[],
    restore: async (stackId: number, fileName: string) =>
      (await rpc('backups.restore', { stackId, fileName })).restore as BackupRunAccepted,
    getStackConfig: async (stackId: number) =>
      (await rpc('backups.getStackConfig', { stackId })).config as BackupStackConfig,
    /**
     * Writes the stack's own backup policy. Every field is tri-state: null clears it and the stack goes
     * back to inheriting (its template's policy when it is a tenant, otherwise the instance default).
     * The whole policy is posted on every call, so an omitted field means "clear it", not "leave it".
     */
    setStackConfig: async (
      stackId: number,
      enabled: boolean | null,
      stopContainers: boolean | null,
      cron: string | null,
      quiesceMode: BackupQuiesceMode | null,
    ) =>
      (await rpc('backups.setStackConfig', {
        stackId, enabled, stopContainers, cron, quiesceMode,
      })).config as BackupStackConfig,

    /** The product Backups tab's read model: the template policies and the fleet rollup. */
    getProductBackups: async (productId: number) =>
      (await rpc('backups.getProductBackups', { productId })) as ProductBackups,

    /**
     * Writes the backup policy a template's instances inherit. Not a fan-out: an instance that set a
     * value of its own keeps it, which is what keeps the inheritance live.
     */
    setTemplatePolicy: async (
      templateId: number,
      enabled: boolean | null,
      stopContainers: boolean | null,
      cron: string | null,
      quiesceMode: BackupQuiesceMode | null,
    ) =>
      (await rpc('backups.setTemplatePolicy', {
        templateId, enabled, stopContainers, cron, quiesceMode,
      })).policy as BackupTemplatePolicy,
    previewPlan: async (stackId: number) =>
      (await rpc('backups.previewPlan', { stackId })).preview as BackupPlanPreview,
    setServiceOverride: async (
      stackId: number,
      service: string,
      override: { exclude: boolean; stop: string | null; dump: string | null },
    ) =>
      (await rpc('backups.setServiceOverride', { stackId, service, ...override }))
        .override as BackupServiceOverride | null,

    /**
     * The fleet-wide twin of `setServiceOverride`: one row on the template that every instance reads
     * live. Same contract, field for field — the whole override is replaced and clearing every knob
     * deletes it. An instance's own row for the same service replaces this one *whole* (precedence is
     * per service, not per knob), and a compose label still beats both.
     */
    setTemplateServiceOverride: async (
      templateId: number,
      service: string,
      override: { exclude: boolean; stop: string | null; dump: string | null },
    ) =>
      (await rpc('backups.setTemplateServiceOverride', { templateId, service, ...override }))
        .override as BackupServiceOverride | null,
  },

  templates: {
    list: async () => (await rpc('templates.list', {})).templates as StackTemplate[],
    get: async (id: number) =>
      (await rpc('templates.get', { id })) as { template: StackTemplate; baseEnvVars: TemplateEnvVar[] },
    create: async (data: CreateTemplateRequest) =>
      (await rpc('templates.create', {
        name: data.name,
        productId: data.productId ?? null,
        repositoryUrl: data.repositoryUrl,
        composeFilePath: data.composeFilePath,
        branch: data.branch,
        credentialId: data.credentialId ?? null,
        domainPattern: data.domainPattern,
        targetServiceName: data.targetServiceName,
        targetPort: data.targetPort,
        baseEnvVars: data.baseEnvVars ?? null,
        realmId: data.realmId ?? null,
      })).template as StackTemplate,
    update: async (id: number, data: UpdateTemplateRequest) =>
      (await rpc('templates.update', {
        id,
        name: data.name,
        repositoryUrl: data.repositoryUrl,
        composeFilePath: data.composeFilePath,
        branch: data.branch,
        credentialId: data.credentialId ?? null,
        domainPattern: data.domainPattern,
        targetServiceName: data.targetServiceName,
        targetPort: data.targetPort,
        baseEnvVars: data.baseEnvVars ?? null,
        realmId: data.realmId ?? null,
      })).template as StackTemplate,
    delete: async (id: number) => {
      await rpc('templates.delete', { id })
    },
    addTenant: async (data: AddTenantRequest) =>
      (await rpc('templates.addTenant', {
        templateId: data.templateId,
        slug: data.slug,
        envOverrides: data.envOverrides ?? null,
      })).tenant as Tenant,
    /**
     * Adopts an existing standalone stack of this setup's product as the tenant `slug`. The stack keeps
     * its containers, volumes, data, name, compose project, environment values and version — only the
     * tenancy link, the missing base env keys and a managed route are added, and nothing is redeployed.
     *
     * Every refusal is a sentence naming what is in the way (already a tenant of X, runs product Y,
     * slug held by stack Z, domain routed to W) — surface them verbatim.
     */
    adoptStack: async (templateId: number, stackId: number, slug: string) =>
      (await rpc('templates.adoptStack', { templateId, stackId, slug })) as AdoptStackResult,
    listTenants: async (templateId: number) =>
      (await rpc('templates.listTenants', { templateId })).tenants as Tenant[],
    deployAll: async (templateId: number) =>
      (await rpc('templates.deployAll', { templateId })).count as number,
    /**
     * Removes a tenant. With `finalBackup` the removal becomes asynchronous: the response says
     * `removed: false` with a `backupEventId`, and the tenant disappears when that backup succeeds. A
     * failed backup aborts the removal and the tenant stays.
     */
    removeTenant: async (
      templateId: number, slug: string, removeVolumes: boolean, finalBackup = false) =>
      (await rpc('templates.removeTenant', { templateId, slug, removeVolumes, finalBackup })) as {
        slug: string
        removed: boolean
        backupEventId?: number | null
      },
    listGrants: async (templateId: number) =>
      (await rpc('templates.listGrants', { templateId })).grants as TemplateGrant[],
    grantManagement: async (templateId: number, stackId: number, allowDelete: boolean) =>
      (await rpc('templates.grantManagement', { templateId, stackId, allowDelete }))
        .grant as TemplateGrant,
    revokeManagement: async (templateId: number, stackId: number) =>
      (await rpc('templates.revokeManagement', { templateId, stackId })).removed as boolean,

    /**
     * One version policy for the whole fleet: pins every current tenant to `releaseId` — or clears
     * every pin with null — **and** stores it as the template's default for tenants created later.
     *
     * `deploy` defaults to false, unlike `stacks.setRelease`: a fleet redeploying is an event to opt
     * into. Refusals are the pin pre-flight's (`409` for a missing digest, a business-rule error for a
     * registry that did not answer) plus `409` for a Git-mode product and a validation error for a
     * release of another product — surface them verbatim.
     */
    setTenantsRelease: async (
      templateId: number, releaseId: number | null, deploy: boolean, backupFirst = false) =>
      (await rpc('templates.setTenantsRelease', { templateId, releaseId, deploy, backupFirst }))
        .result as SetTenantsReleaseResult,

    /**
     * Backs up every instance of a template — the backup twin of `deployAll`, and what an operator
     * presses before a risky fleet change. Serial: the backup queue is single-flight process-wide, so
     * the returned count is what was *queued*, not what has run.
     */
    backupAll: async (templateId: number) =>
      (await rpc('templates.backupAll', { templateId })).count as number,
  },

  metrics: {
    host: async (range?: MetricsRange | null) =>
      (await rpc('metrics.host', { range: range ?? null })).host as HostMetrics,
    containers: async (project?: string | null, range?: MetricsRange | null) =>
      (await rpc('metrics.containers', { project: project ?? null, range: range ?? null }))
        .containers as ContainerMetrics[],
    stacks: async (range?: MetricsRange | null) =>
      (await rpc('metrics.stacks', { range: range ?? null })) as StackMetricsResult,
    getConfig: async () => (await rpc('metrics.getConfig', {})).config as MetricsConfig,
    updateConfig: async (data: UpdateMetricsConfigRequest) =>
      (await rpc('metrics.updateConfig', {
        backend: data.backend,
        retentionDays: data.retentionDays,
        influxUrl: data.influxUrl ?? null,
        influxOrg: data.influxOrg ?? null,
        influxBucket: data.influxBucket ?? null,
        influxToken: data.influxToken ?? null,
        influxComposeProjectTag: data.influxComposeProjectTag ?? null,
        influxDiskMountpoint: data.influxDiskMountpoint ?? null,
      })).config as MetricsConfig,
  },

  users: {
    // Omitting realmId lists every realm's accounts — the management UI is operator-only and sees them
    // all; passing one narrows the roster to that population.
    list: async (realmId?: number | null) =>
      (await rpc('users.list', { realmId: realmId ?? null })).users as User[],
    create: async (data: CreateUserRequest) =>
      (await rpc('users.create', {
        userName: data.userName,
        password: data.password,
        email: data.email ?? null,
        isAdmin: data.isAdmin,
        realmId: data.realmId ?? null,
      })).user as User,
    update: async (id: number, data: UpdateUserRequest) =>
      (await rpc('users.update', {
        id,
        userName: data.userName,
        email: data.email ?? null,
        isAdmin: data.isAdmin,
      })).user as User,
    // Also signs the account out everywhere — see the backend handler.
    resetPassword: async (id: number, newPassword: string) => {
      await rpc('users.resetPassword', { id, newPassword })
    },
    setDisabled: async (id: number, disabled: boolean) =>
      (await rpc('users.setDisabled', { id, disabled })).user as User,
    // Clears the account's two-factor enrolment: the flag, the authenticator key and every unused recovery
    // code. One-directional by design — there is no call that turns a second factor *on* for someone else.
    // `wasEnabled` reports what was actually undone, so the UI can say "cleared" rather than "done".
    resetMfa: async (id: number) =>
      (await rpc('users.resetMfa', { id })).wasEnabled as boolean,
    delete: async (id: number) => {
      await rpc('users.delete', { id })
    },
  },

  groups: {
    // Same realm scoping as users.list: omitted lists every population, supplied narrows to one.
    list: async (realmId?: number | null) =>
      (await rpc('groups.list', { realmId: realmId ?? null })).groups as Group[],
    create: async (name: string, realmId?: number | null) =>
      (await rpc('groups.create', { name, realmId: realmId ?? null })).group as Group,
    rename: async (id: number, name: string) =>
      (await rpc('groups.rename', { id, name })).group as Group,
    delete: async (id: number) => {
      await rpc('groups.delete', { id })
    },
    getMembers: async (id: number) => (await rpc('groups.getMembers', { id })).userIds as number[],
    // Whole-set replace: the members dialog knows the set it wants, so it says so rather than
    // reconstructing a sequence of adds and removes the server would have to trust.
    setMembers: async (id: number, userIds: number[]) =>
      (await rpc('groups.setMembers', { id, userIds })).userIds as number[],
  },

  realms: {
    list: async () => (await rpc('realms.list', {})).realms as Realm[],
    create: async (data: CreateRealmRequest) =>
      (await rpc('realms.create', {
        name: data.name,
        slug: data.slug,
        loginDomain: data.loginDomain ?? null,
      })).realm as Realm,
    // A partial update, unlike every other update on this facade: null means "leave this field alone", so
    // `?? null` here folds an omitted field into "leave alone" rather than into a cleared value. Clearing
    // the login route is therefore `0`, which survives the `??` — the caller says which of the two it
    // means by omitting the field or passing 0.
    update: async (id: number, data: UpdateRealmRequest) =>
      (await rpc('realms.update', {
        id,
        name: data.name ?? null,
        loginRouteId: data.loginRouteId ?? null,
      })).realm as Realm,
    remove: async (id: number) => {
      await rpc('realms.delete', { id })
    },
  },

  audit: {
    // Read-only, and deliberately the whole surface: the trail is written by the planes whose acts it
    // records, never from here. A page is a keyset cursor rather than an offset — the trail is being
    // appended to while it is read, so an offset would shift under the reader between "load more" clicks.
    listEvents: async (query: AuditQuery = {}) =>
      (await rpc('audit.listEvents', {
        category: query.category ?? null,
        action: query.action ?? null,
        actor: query.actor ?? null,
        beforeId: query.beforeId ?? null,
        limit: query.limit ?? null,
      })) as AuditEventPage,
    // The values actually present, so the filters offer what is there rather than a frontend constant
    // that drifts the moment a new writer lands.
    facets: async () => (await rpc('audit.listFacets', {})) as AuditFacets,
  },

  system: {
    getSelf: async () => (await rpc('system.getSelf', {})).status as SelfUpdateStatus,
    updateConfig: async (data: UpdateSelfConfigRequest) =>
      (await rpc('system.updateConfig', {
        credentialId: data.credentialId ?? null,
      })).status as SelfUpdateStatus,
    check: async () => (await rpc('system.check', {})).status as SelfUpdateStatus,
    update: async () => {
      await rpc('system.applyUpdate', {})
    },
    dockerConfig: async () => (await rpc('system.dockerConfig', {})).config as DockerConfigStatus,
    getAutomation: async () => (await rpc('system.getAutomation', {})) as AutomationConfig,
    updateAutomation: async (data: UpdateAutomationRequest) =>
      (await rpc('system.updateAutomation', {
        autoCheckEnabled: data.autoCheckEnabled,
        autoCheckIntervalMinutes: data.autoCheckIntervalMinutes,
        stackCheckEnabled: data.stackCheckEnabled,
        stackCheckIntervalMinutes: data.stackCheckIntervalMinutes,
        imagePruneEnabled: data.imagePruneEnabled,
        imagePruneIntervalMinutes: data.imagePruneIntervalMinutes,
      })) as AutomationConfig,
    getAuthConfig: async () => (await rpc('system.getAuthConfig', {})) as AuthConfig,
    updateAuthConfig: async (data: UpdateAuthConfigRequest) =>
      (await rpc('system.updateAuthConfig', {
        enabled: data.enabled,
        host: data.host ?? null,
        sessionLifetimeHours: data.sessionLifetimeHours,
        absoluteSessionLifetimeDays: data.absoluteSessionLifetimeDays,
      })) as AuthConfig,
  },
}
