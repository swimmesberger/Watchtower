// Thin ergonomic wrapper over the generated JSON-RPC client. Route components keep calling
// `api.stacks.list()` etc.; each method issues a typed JSON-RPC call and unwraps the envelope.
// Nullable params are built explicitly (`?? null`) because the generated param types require
// every key to be present.
import { rpc } from './rpc-client'
import type {
  ActiveDeployment,
  Container,
  CreateCredentialRequest,
  CreateRegistryRequest,
  CreateStackRequest,
  Credential,
  DeployAccepted,
  DeployEvent,
  DockerConfigStatus,
  Registry,
  SelfUpdateStatus,
  Stack,
  StackEnvVar,
  StackEnvVarInput,
  UpdateCredentialRequest,
  UpdateRegistryRequest,
  UpdateSelfConfigRequest,
  UpdateStackRequest,
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
  },
}
