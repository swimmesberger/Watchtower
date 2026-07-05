import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import tsConfigPaths from 'vite-tsconfig-paths'

export default defineConfig({
  plugins: [react(), tailwindcss(), tsConfigPaths()],
  // Hook-using libraries must resolve to this app's single instance. Vite's dep optimizer otherwise
  // pre-bundles a second React — surfacing as "Invalid hook call" at the first useContributions — and
  // this hits published installs of @swimmesberger/elarion-contributions too (not just linked ones), so
  // the dedupe + optimizeDeps below stay required (Elarion #71). @tanstack/react-router is deduped for
  // the same reason now that the statically-typed router relies on a single router instance.
  resolve: { dedupe: ['react', 'react-dom', '@tanstack/react-router'] },
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
