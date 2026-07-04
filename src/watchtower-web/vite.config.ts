import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import tsConfigPaths from 'vite-tsconfig-paths'

export default defineConfig({
  plugins: [react(), tailwindcss(), tsConfigPaths()],
  server: {
    // During development, proxy API calls to the .NET backend (see launchSettings.json).
    proxy: {
      '/rpc': 'http://localhost:5080',
      '/api': 'http://localhost:5080',
      '/health': 'http://localhost:5080',
    },
  },
  build: {
    // Output to dist/ which is copied to wwwroot/ in the Docker image.
    outDir: 'dist',
  },
})
