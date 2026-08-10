// Thin ergonomic wrapper over the generated JSON-RPC client. Route components keep calling
// `api.stacks.list()` etc.; each method issues a typed JSON-RPC call and unwraps the envelope.
// Nullable params are built explicitly (`?? null`) because the generated param types require
// every key to be present.
import { rpc } from './rpc-client'
import type {
  ActiveDeployment,
  AutomationConfig,
  Container,
  ContainerEnvVar,
  ContainerMetrics,
  AddTenantRequest,
  CreateCredentialRequest,
  CreateRealmRequest,
  CreateRegistryRequest,
  CreateRouteRequest,
  CreateStackRequest,
  CreateTemplateRequest,
  Credential,
  DeployAccepted,
  DeployEvent,
  DnsCheckResult,
  DockerConfigStatus,
  Group,
  HostMetrics,
  MetricsConfig,
  MetricsRange,
  NetworkInfo,
  NetworkPortsResult,
  PruneOrphansResult,
  ProxyStatus,
  Realm,
  Registry,
  Route,
  RouteAccess,
  RouteAccessView,
  SelfUpdateStatus,
  Stack,
  StackTemplate,
  Tenant,
  TemplateEnvVar,
  TemplateGrant,
  UpdateTemplateRequest,
  StackEnvVar,
  StackEnvVarInput,
  StackMetricsResult,
  UpdateCredentialRequest,
  UpdateMetricsConfigRequest,
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

export const api = {
  registries: {
    list: async () => (await rpc('registries.list', {})).registries as Registry[],
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

  stacks: {
    list: async () => (await rpc('stacks.list', {})).stacks as Stack[],
    get: async (id: number) => (await rpc('stacks.get', { id })).stack as Stack,
    create: async (data: CreateStackRequest) =>
      (await rpc('stacks.create', {
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
    events: async (id: number) => (await rpc('stacks.events', { stackId: id })).events as DeployEvent[],
    getEnv: async (id: number) => (await rpc('stacks.getEnv', { stackId: id })).envVars as StackEnvVar[],
    setEnv: async (id: number, vars: StackEnvVarInput[]) =>
      (await rpc('stacks.setEnv', { stackId: id, vars })).envVars as StackEnvVar[],
    checkUpdates: async (id: number) => (await rpc('stacks.checkUpdates', { id })).stack as Stack,
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
      })).route as Route,
    updateRoute: async (id: number, data: UpdateRouteRequest) =>
      (await rpc('proxy.updateRoute', {
        id,
        domain: data.domain,
        serviceName: data.serviceName,
        containerPort: data.containerPort,
        tlsEnabled: data.tlsEnabled,
        isPrimary: data.isPrimary,
      })).route as Route,
    deleteRoute: async (id: number) => {
      await rpc('proxy.deleteRoute', { id })
    },
    checkDns: async (domain: string) =>
      (await rpc('proxy.checkDns', { domain })) as DnsCheckResult,
    getStatus: async () => (await rpc('proxy.getStatus', {})) as ProxyStatus,
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

  templates: {
    list: async () => (await rpc('templates.list', {})).templates as StackTemplate[],
    get: async (id: number) =>
      (await rpc('templates.get', { id })) as { template: StackTemplate; baseEnvVars: TemplateEnvVar[] },
    create: async (data: CreateTemplateRequest) =>
      (await rpc('templates.create', {
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
    listTenants: async (templateId: number) =>
      (await rpc('templates.listTenants', { templateId })).tenants as Tenant[],
    deployAll: async (templateId: number) =>
      (await rpc('templates.deployAll', { templateId })).count as number,
    removeTenant: async (templateId: number, slug: string, removeVolumes: boolean) =>
      (await rpc('templates.removeTenant', { templateId, slug, removeVolumes })).slug as string,
    listGrants: async (templateId: number) =>
      (await rpc('templates.listGrants', { templateId })).grants as TemplateGrant[],
    grantManagement: async (templateId: number, stackId: number, allowDelete: boolean) =>
      (await rpc('templates.grantManagement', { templateId, stackId, allowDelete }))
        .grant as TemplateGrant,
    revokeManagement: async (templateId: number, stackId: number) =>
      (await rpc('templates.revokeManagement', { templateId, stackId })).removed as boolean,
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
        authHost: data.authHost ?? null,
      })).realm as Realm,
    // A partial update, unlike every other update on this facade: null means "leave this field alone", so
    // `?? null` here folds an omitted field into "leave alone" rather than into a cleared value. Clearing
    // the auth host is therefore an empty string, which survives the `??` — the caller says which of the
    // two it means by omitting the field or passing ''.
    update: async (id: number, data: UpdateRealmRequest) =>
      (await rpc('realms.update', {
        id,
        name: data.name ?? null,
        authHost: data.authHost ?? null,
      })).realm as Realm,
    remove: async (id: number) => {
      await rpc('realms.delete', { id })
    },
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
    updateAutomation: async (data: AutomationConfig) =>
      (await rpc('system.updateAutomation', {
        autoCheckEnabled: data.autoCheckEnabled,
        autoCheckIntervalMinutes: data.autoCheckIntervalMinutes,
        stackCheckEnabled: data.stackCheckEnabled,
        stackCheckIntervalMinutes: data.stackCheckIntervalMinutes,
      })) as AutomationConfig,
  },
}
