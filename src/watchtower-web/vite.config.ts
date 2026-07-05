import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import tsConfigPaths from 'vite-tsconfig-paths'

export default defineConfig({
  plugins: [react(), tailwindcss(), tsConfigPaths()],
  // The elarion-contributions React adapter creates its own React context; it must share the app's
  // single React instance (dedupe) and be pre-bundled against it (optimizeDeps) or hooks resolve twice.
  resolve: { dedupe: ['react', 'react-dom'] },
  optimizeDeps: {
    include: [
      '@swimmesberger/elarion-contributions',
      '@swimmesberger/elarion-contributions/react',
      '@swimmesberger/elarion-contributions/tanstack-router',
    ],
  },
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
