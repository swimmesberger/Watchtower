// Web Push on this device: the service worker (public/sw.js) receives deploy-failure alerts from the
// browser's push service even while Watchtower is closed. Needs notification permission and the production
// service worker — in the Vite dev server nothing is registered, so everything here reports "unavailable".
//
// Generic plumbing, kept in this one file so it can be swapped for a shared Elarion package once the
// framework ships Web Push (TODO(elarion#162)). The server half is the Notifications module.
import { rpc } from './rpc-client'

/**
 * What this device can do about push, which decides what the UI offers:
 * - `available` — push works here; offer the switch.
 * - `install-first` — iPhone/iPad Safari in a tab. iOS (16.4+) only exposes Web Push to a web app added to
 *   the Home Screen, so the honest offer is "install it first", not a switch that can never turn on.
 * - `unsupported` — no Push API at all (old browser, private mode, the dev server without a worker).
 */
export type PushAvailability = 'available' | 'install-first' | 'unsupported'

function pushApisPresent(): boolean {
  return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window
}

/** iPhone/iPad, including iPadOS reporting itself as a Mac (touch points give it away). */
export function isIos(): boolean {
  const ua = navigator.userAgent
  return /iPad|iPhone|iPod/.test(ua) || (ua.includes('Macintosh') && navigator.maxTouchPoints > 1)
}

/** Running as an installed app (Home Screen) rather than in a browser tab. */
export function isStandalone(): boolean {
  const legacy = (navigator as Navigator & { standalone?: boolean }).standalone
  return legacy === true || window.matchMedia?.('(display-mode: standalone)').matches === true
}

export function pushAvailability(): PushAvailability {
  if (pushApisPresent()) return 'available'
  if (isIos() && !isStandalone()) return 'install-first'
  return 'unsupported'
}

function toApplicationServerKey(base64Url: string): Uint8Array<ArrayBuffer> {
  const padded = base64Url + '='.repeat((4 - (base64Url.length % 4)) % 4)
  const raw = atob(padded.replace(/-/g, '+').replace(/_/g, '/'))
  const bytes = new Uint8Array(new ArrayBuffer(raw.length))
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i)
  return bytes
}

/** The active registration, or null when there is none (dev server, worker failed to install). */
async function activeRegistration(): Promise<ServiceWorkerRegistration | null> {
  if (!pushApisPresent()) return null
  const registration = await navigator.serviceWorker.getRegistration()
  if (!registration) return null
  return navigator.serviceWorker.ready
}

/** Subscribes this browser with the server, or re-registers an existing subscription. */
export async function subscribeToPush(): Promise<boolean> {
  if (!pushApisPresent() || Notification.permission !== 'granted') return false
  const registration = await activeRegistration()
  if (!registration) return false

  let subscription = await registration.pushManager.getSubscription()
  if (!subscription) {
    const { publicKey } = await rpc('notifications.publicKey', {})
    subscription = await registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: toApplicationServerKey(publicKey),
    })
  }
  const { endpoint, keys } = subscription.toJSON()
  if (!endpoint || !keys?.p256dh || !keys?.auth) return false
  await rpc('notifications.subscribe', {
    endpoint,
    p256dh: keys.p256dh,
    auth: keys.auth,
    userAgent: navigator.userAgent,
  })
  return true
}

/**
 * Asks for permission and subscribes. Must be called straight from a click handler: iOS only shows the
 * permission prompt in response to a user gesture, and an `await` before `requestPermission` loses it.
 */
export async function enablePush(): Promise<NotificationPermission> {
  const permission = await Notification.requestPermission()
  if (permission === 'granted') await subscribeToPush()
  return permission
}

/** Removes this device's subscription on the server and in the browser. Never throws. */
export async function unsubscribeFromPush(): Promise<void> {
  try {
    const registration = await activeRegistration()
    const subscription = await registration?.pushManager.getSubscription()
    if (!subscription) return
    try {
      await rpc('notifications.unsubscribe', { endpoint: subscription.endpoint })
    } finally {
      await subscription.unsubscribe()
    }
  } catch {
    // Best effort: a server that no longer knows the endpoint drops it at the next send anyway.
  }
}

export async function isPushSubscribed(): Promise<boolean> {
  const registration = await activeRegistration()
  return !!(await registration?.pushManager.getSubscription())
}

/**
 * Re-registers an existing subscription on app start. Heals a subscription the push service rotated while
 * the app was closed, or one the server dropped. Silent: never prompts, never throws.
 */
export async function refreshPushSubscription(): Promise<void> {
  try {
    if (!pushApisPresent() || Notification.permission !== 'granted') return
    if (await isPushSubscribed()) await subscribeToPush()
  } catch {
    // Nothing to tell the user at startup; the settings switch shows the real state.
  }
}
