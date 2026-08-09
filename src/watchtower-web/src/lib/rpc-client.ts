import { rpcResultSchemas } from '@/generated/rpc-schemas'
import type { RpcMethods } from '@/generated/rpc-types'
import { apiBase } from './config'
import { goToLogin, LOGIN_PATH } from './auth'
import { randomUuid } from './utils'

/**
 * A JSON-RPC error returned by the Watchtower API. The numeric codes map to Elarion's
 * transport-neutral <c>AppError</c> categories.
 */
export class RpcError extends Error {
  constructor(
    public readonly code: number,
    message: string,
    public readonly data?: unknown
  ) {
    super(message)
    this.name = 'RpcError'
  }

  get isNotFound() {
    return this.code === -32001
  }
  get isConflict() {
    return this.code === -32002
  }
  get isForbidden() {
    return this.code === -32003
  }
  get isUnauthorized() {
    return this.code === -32005
  }
  get isValidation() {
    return this.code === -32602
  }
}

/**
 * Calls a typed JSON-RPC method against the backend and validates the response with the
 * generated Zod schema. Throws {@link RpcError} on an application error.
 */
export async function rpc<M extends keyof RpcMethods>(
  method: M,
  params: RpcMethods[M]['params']
): Promise<RpcMethods[M]['result']> {
  const response = await fetch(`${apiBase}/rpc`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    // Carries the __wt_sso cookie; required cross-origin (Vite dev server / Aspire) and harmless same-origin.
    credentials: 'include',
    body: JSON.stringify({ jsonrpc: '2.0', method, params, id: randomUuid() }),
  })

  if (!response.ok) {
    throw new Error(`RPC transport error: ${response.status} ${response.statusText}`)
  }

  const json = await response.json()
  if (json.error) {
    const error = new RpcError(json.error.code, json.error.message, json.error.data)
    // A session that expired mid-visit surfaces as -32005 on whatever call happened to run next. Bounce
    // once, from here, so every caller gets the behaviour without each query wiring up its own handler —
    // the guard on the login page itself is what stops this from looping.
    if (error.isUnauthorized && window.location.pathname !== LOGIN_PATH) goToLogin()
    throw error
  }

  const schema = rpcResultSchemas[method]
  return schema.parse(json.result) as RpcMethods[M]['result']
}
