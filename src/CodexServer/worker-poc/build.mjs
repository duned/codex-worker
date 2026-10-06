import { build } from 'esbuild';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
if (!process.argv[2]) throw new Error('An output directory is required.');
await build({
  entryPoints: ['src/poc.jsx'],
  outdir: resolve(process.argv[2]),
  bundle: true,
  minify: true,
  format: 'iife',
  target: ['es2022'],
  jsx: 'automatic',
  define: { 'process.env.NODE_ENV': '"production"' },
  legalComments: 'inline',
  banner: { js: '/*!\n' + readFileSync('UNTITLED-UI-LICENSE', 'utf8') + '\n*/' }
});
