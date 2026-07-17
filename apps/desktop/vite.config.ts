import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'path'

const host = process.env.TAURI_DEV_HOST
const devPort = Number.parseInt(process.env.WINGMAN_DEV_PORT ?? '4173', 10)

if (!Number.isInteger(devPort) || devPort < 1 || devPort > 65534) {
  throw new Error(`Invalid WINGMAN_DEV_PORT: ${process.env.WINGMAN_DEV_PORT}`)
}

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  clearScreen: false,
  server: {
    port: devPort,
    strictPort: true,
    host: host || '127.0.0.1',
    hmr: host ? { protocol: 'ws', host, port: devPort + 1 } : undefined,
    watch: {
      ignored: ['**/src-tauri/**'],
    },
  },
})
