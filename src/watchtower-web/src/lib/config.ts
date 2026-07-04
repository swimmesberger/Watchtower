// The API base URL.
//
// - Empty (same-origin) in production, where the SPA is served from the API's wwwroot, and in a
//   standalone `npm run dev`, where Vite proxies /rpc, /api and /health to the backend.
// - Under the .NET Aspire AppHost it is injected as VITE_API_URL — an absolute URL to the API
//   resource — so the JSON-RPC client, the SSE streams and the webhook URLs all target it directly.
export const apiBase = (import.meta.env.VITE_API_URL ?? '').replace(/\/+$/, '')
