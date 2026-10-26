import { defineConfig } from 'vite';

export default defineConfig(({ command }) => ({
  base: './',
  server: { host: '127.0.0.1',port: 5173,strictPort: true,proxy: { '/rpc': 'http://127.0.0.1:4317' } },
  plugins: [{ name: 'desktop-content-policy',transformIndexHtml: (html: string) => html.replace('MC_CONNECT_POLICY',command==='serve'? "'self' ws://localhost:5173 ws://127.0.0.1:5173 ipc: http://ipc.localhost":"ipc: http://ipc.localhost") }],
  build: { target: 'es2022',sourcemap: false },
}));
