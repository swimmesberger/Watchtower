import { rpcResultSchemas } from '@/generated/rpc-schemas'
import type { RpcMethods } from '@/generated/rpc-types'
import { apiBase } from './config'

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
    body: JSON.stringify({ jsonrpc: '2.0', method, params, id: crypto.randomUUID() }),
  })

  if (!response.ok) {
    throw new Error(`RPC transport error: ${response.status} ${response.statusText}`)
  }

  const json = await response.json()
  if (json.error) {
    throw new RpcError(json.error.code, json.error.message, json.error.data)
  }

  const schema = rpcResultSchemas[method]
  return schema.parse(json.result) as RpcMethods[M]['result']
}
