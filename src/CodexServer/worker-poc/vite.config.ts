import { defineConfig } from 'vite';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import tailwindcss from '@tailwindcss/postcss';

export default defineConfig(({ command }) => ({
  base: command === 'serve' ? '/dashboard-preview/' : '/dashboard-assets/preview/',
  esbuild: { jsx: 'automatic' },
  resolve: { alias: { '@': resolve(import.meta.dirname, 'src/untitled') } },
  css: { postcss: { plugins: [tailwindcss()] } },
  build: {
    manifest: true, target: 'es2022',
    rollupOptions: {
      // Upstream client directives have no meaning in a browser-only SPA.
      onwarn(warning, warn) {
        if (warning.code === 'MODULE_LEVEL_DIRECTIVE' && warning.message.includes('use client')) return;
        if (warning.code === 'SOURCEMAP_ERROR' && warning.message.includes("Can't resolve original location")) return;
        warn(warning);
      },
      output: {
        banner: '/*!\n' + readFileSync(resolve(import.meta.dirname, 'UNTITLED-UI-LICENSE'), 'utf8') + '\n*/',
        manualChunks(id) {
          if (/node_modules\/(react|react-dom|scheduler)\//.test(id)) return 'react';
        }
      }
    }
  },
  server: {
    // Point at an HTTPS proxy exposing the configured administration origin.
    // Do not rewrite Origin/Host or bypass Server session authorization.
    proxy: process.env.DASHBOARD_API_ORIGIN ? {
      '/api': { target: process.env.DASHBOARD_API_ORIGIN, changeOrigin: false }
    } : undefined
  }
}));
