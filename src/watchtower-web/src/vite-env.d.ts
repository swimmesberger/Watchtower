/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Absolute API base URL injected by the Aspire AppHost; unset for same-origin (proxy/production). */
  readonly VITE_API_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
