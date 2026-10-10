import { defineConfig } from 'vite'

export default defineConfig({
  optimizeDeps: { exclude: ['@jsquash/jpeg', '@jsquash/resize'] },
  build: {
    target: ['chrome111', 'edge111', 'safari16.4', 'firefox128'],
    assetsInlineLimit: 0,
  },
  worker: { format: 'es' },
})
