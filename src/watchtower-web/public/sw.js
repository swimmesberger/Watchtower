// Watchtower service worker — what makes the home-screen install work, and what receives Web Push.
//
//   Navigations (the SPA shell)    network-first; the cached shell only when the network is gone, so a
//                                  new build reaches an installed app on its next open instead of never.
//   /assets/* (content-hashed)     cache-first: immutable by construction.
//   Manifest + icons               stale-while-revalidate.
//   Everything else                not touched — the API (/rpc, /api, /mcp, /health), the SSE log streams,
//                                  ACME challenges, cross-origin requests. A cached answer from any of those
//                                  would be a stale status, a replayed log or a wrong session.
//
// Bump CACHE whenever the shell list changes; activate() drops every other cache.
const CACHE = 'watchtower-v1'
const STATIC_FILES = [
  '/manifest.webmanifest',
  '/favicon.svg',
  '/icon-192.png',
  '/icon-512.png',
  '/icon-maskable-512.png',
  '/apple-touch-icon.png',
]
const SERVER_PREFIXES = ['/rpc', '/api/', '/mcp', '/health', '/.well-known/']

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((c) => c.addAll(STATIC_FILES)))
  self.skipWaiting()
})

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))),
  )
  self.clients.claim()
})

self.addEventListener('fetch', (event) => {
  const { request } = event
  if (request.method !== 'GET') return
  const url = new URL(request.url)
  if (url.origin !== self.location.origin) return
  if (SERVER_PREFIXES.some((p) => url.pathname.startsWith(p))) return

  if (request.mode === 'navigate') {
    event.respondWith(networkFirstShell(request))
  } else if (url.pathname.startsWith('/assets/')) {
    event.respondWith(cacheFirst(request))
  } else if (STATIC_FILES.includes(url.pathname)) {
    event.respondWith(staleWhileRevalidate(request))
  }
})

// Every client-side route is served index.html, so the shell is cached under the single key "/". Only an
// HTML 200 is stored: a navigation that an auth proxy in front of Watchtower answered with its own login
// page or a redirect must not become the offline shell.
async function networkFirstShell(request) {
  const cache = await caches.open(CACHE)
  try {
    const response = await fetch(request)
    if (response.ok && !response.redirected && response.headers.get('content-type')?.includes('text/html'))
      cache.put('/', response.clone())
    return response
  } catch {
    return (await cache.match('/')) ?? Response.error()
  }
}

async function cacheFirst(request) {
  const cache = await caches.open(CACHE)
  const cached = await cache.match(request)
  if (cached) return cached
  const response = await fetch(request)
  if (response.ok) cache.put(request, response.clone())
  return response
}

async function staleWhileRevalidate(request) {
  const cache = await caches.open(CACHE)
  const cached = await cache.match(request)
  const refresh = fetch(request)
    .then((response) => {
      if (response.ok) cache.put(request, response.clone())
      return response
    })
    .catch(() => undefined)
  return cached ?? (await refresh) ?? Response.error()
}

// ── Web Push ──────────────────────────────────────────────────────────────────────────────────────────
// Generic push plumbing, kept in one block so it can be swapped for a shared Elarion service-worker module
// once the framework ships Web Push (TODO(elarion#162)). Payload: {title, body, url, tag}, as sent by the
// Notifications module's PushNotifier. `tag` collapses repeats for the same subject (one per stack), so a
// stack that fails three times shows one, updated, notification.

self.addEventListener('push', (event) => {
  let data = { title: 'Watchtower', body: '', url: '/', tag: undefined }
  try {
    data = { ...data, ...event.data?.json() }
  } catch {
    data.body = event.data?.text() ?? ''
  }
  event.waitUntil(
    self.registration.showNotification(data.title, {
      body: data.body,
      tag: data.tag,
      icon: '/icon-192.png',
      badge: '/icon-192.png',
      data: { url: data.url },
    }),
  )
})

// Tap → focus the running app (or open one) at the notification's in-app URL.
self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const url = new URL(event.notification.data?.url ?? '/', self.location.origin).href
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(async (clients) => {
      const existing = clients.find((c) => c.url.startsWith(self.location.origin))
      if (existing) {
        await existing.focus()
        if ('navigate' in existing) await existing.navigate(url)
      } else {
        await self.clients.openWindow(url)
      }
    }),
  )
})

// The push service rotated the subscription: re-subscribe and tell the server directly, since the app may
// not be open. Rides on the session cookie; if that has expired the call fails quietly and the app's own
// re-subscribe on its next start repairs it.
self.addEventListener('pushsubscriptionchange', (event) => {
  const options = event.oldSubscription?.options ?? { userVisibleOnly: true }
  event.waitUntil(
    self.registration.pushManager.subscribe(options).then((subscription) => {
      const { endpoint, keys } = subscription.toJSON()
      return fetch('/rpc', {
        method: 'POST',
        credentials: 'include',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          jsonrpc: '2.0',
          id: 1,
          method: 'notifications.subscribe',
          params: { endpoint, p256dh: keys.p256dh, auth: keys.auth },
        }),
      })
    }),
  )
})
