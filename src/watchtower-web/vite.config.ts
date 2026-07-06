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
    // Match tsconfig's ES2022 target (Vite's default 'modules' baseline is ~es2020, which rejects
    // syntax tsc accepts — e.g. top-level await). ES2022 is our minimum browser floor: modern
    // evergreen browsers only, which suits a self-hosted admin tool behind an auth proxy.
    target: 'es2022',
  },
})
