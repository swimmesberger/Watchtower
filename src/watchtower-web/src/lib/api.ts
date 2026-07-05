// Thin ergonomic wrapper over the generated JSON-RPC client. Route components keep calling
// `api.stacks.list()` etc.; each method issues a typed JSON-RPC call and unwraps the envelope.
// Nullable params are built explicitly (`?? null`) because the generated param types require
// every key to be present.
import { rpc } from './rpc-client'
import type {
  ActiveDeployment,
  AutomationConfig,
  Container,
  ContainerMetrics,
  CreateCredentialRequest,
  CreateRegistryRequest,
  CreateStackRequest,
  Credential,
  DeployAccepted,
  DeployEvent,
  DockerConfigStatus,
  HostMetrics,
  NetworkInfo,
  NetworkPortsResult,
  PruneOrphansResult,
  Registry,
  SelfUpdateStatus,
  Stack,
  StackEnvVar,
  StackEnvVarInput,
  StackMetricsResult,
  UpdateCredentialRequest,
  UpdateRegistryRequest,
  UpdateSelfConfigRequest,
  UpdateStackRequest,
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

  metrics: {
    host: async () => (await rpc('metrics.host', {})).host as HostMetrics,
    containers: async (project?: string | null) =>
      (await rpc('metrics.containers', { project: project ?? null })).containers as ContainerMetrics[],
    stacks: async () => (await rpc('metrics.stacks', {})) as StackMetricsResult,
  },

  system: {
    getSelf: async () => (await rpc('system.getSelf', {})).status as SelfUpdateStatus,
    updateConfig: async (data: UpdateSelfConfigRequest) =>
      (await rpc('system.updateConfig', {
        imageName: data.imageName ?? null,
        credentialId: data.credentialId ?? null,
        composeFilePath: data.composeFilePath ?? null,
        composeProjectName: data.composeProjectName ?? null,
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
