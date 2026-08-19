// Self-service two-factor. Not JSON-RPC for the same reason `auth.ts` is not: every management handler is
// gated to the operator realm, and protecting your own account is not management — a customer realm's
// account has to be able to do it too. So the backend exposes these as plain endpoints
// (Watchtower.Api/Endpoints/WatchtowerAuthEndpoints.Mfa.cs) and this is the matching plain fetch.
//
// Every call operates on the *caller's own* account and takes no user id: there is no parameter here
// through which one account could reach another.
import { apiBase } from './config'

/** The account's own view of its second factor. Deliberately carries no key and no codes. */
export interface MfaStatus {
  totpEnabled: boolean
  recoveryCodesRemaining: number
}

/**
 * The enrolment secret, in the two forms an authenticator app takes it: `otpauthUri` behind a QR code, or
 * `sharedKey` typed in by hand when there is no camera. Handed over exactly once — asking again mints a
 * different key — and never persisted anywhere by this client.
 */
export interface MfaEnrolment {
  sharedKey: string
  otpauthUri: string
}

/**
 * Thrown when a code was refused. Distinct from a transport failure because the answer is different: the
 * form stays open and the digits are wrong, rather than the request never having landed.
 */
export class MfaCodeError extends Error {}

/** Thrown when the account is not in the state the operation needs (already enrolled, or not enrolled). */
export class MfaStateError extends Error {}

/** Two-factor state of the signed-in account. */
export function getMfaStatus(): Promise<MfaStatus> {
  return send<MfaStatus>('GET', '/api/auth/mfa')
}

/**
 * Starts enrolment: mints a fresh authenticator key and returns it. Two-factor stays *off* until
 * {@link confirmTotp} proves the app is really set up, so an abandoned enrolment cannot lock anyone out.
 * Refused with {@link MfaStateError} while two-factor is already on — replacing the key of an enabled
 * account would invalidate the authenticator its owner is actually using.
 */
export function beginTotp(): Promise<MfaEnrolment> {
  return send<MfaEnrolment>('POST', '/api/auth/mfa/totp/begin', {})
}

/**
 * Finishes enrolment and returns the recovery codes. This is the only time they are readable — the server
 * keeps hashes — so a caller that drops them has thrown them away for good.
 */
export async function confirmTotp(code: string): Promise<string[]> {
  const body = await send<{ recoveryCodes: string[] }>('POST', '/api/auth/mfa/totp/confirm', { code })
  return body.recoveryCodes
}

/**
 * Turns two-factor off. Accepts an authenticator code *or* a recovery code, because someone whose phone is
 * gone still needs a way out that does not require an administrator.
 */
export function disableTotp(code: string): Promise<MfaStatus> {
  return send<MfaStatus>('POST', '/api/auth/mfa/totp/disable', { code })
}

/**
 * Replaces the recovery codes with a fresh set and returns it; whatever was left of the old set stops
 * working. Requires an authenticator code specifically — spending a recovery code to mint ten more would
 * turn one leaked code into permanent access.
 */
export async function regenerateRecoveryCodes(code: string): Promise<string[]> {
  const body = await send<{ recoveryCodes: string[] }>(
    'POST',
    '/api/auth/mfa/recovery/regenerate',
    { code },
  )
  return body.recoveryCodes
}

/**
 * One request shape for the whole surface. `credentials: 'include'` is what sends the `__wt_sso` cookie
 * (it is `HttpOnly`, so nothing here ever sees it) and is required for the Aspire/Vite dev setup where the
 * SPA and the API are different origins. The JSON content type is not decoration: the backend refuses
 * anything else, which is what stops a cross-site form from switching somebody's second factor off.
 */
async function send<T>(method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
  const response = await fetch(`${apiBase}${path}`, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  if (response.status === 401) {
    throw new MfaCodeError(await messageOf(response, 'That code is not valid.'))
  }
  if (response.status === 409) {
    throw new MfaStateError(await messageOf(response, 'That is not possible in the current state.'))
  }
  if (!response.ok) {
    throw new Error(`Request failed: ${response.status} ${response.statusText}`)
  }

  return (await response.json()) as T
}

async function messageOf(response: Response, fallback: string): Promise<string> {
  const body = (await response.json().catch(() => null)) as { message?: string } | null
  return body?.message ?? fallback
}
