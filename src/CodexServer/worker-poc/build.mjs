import { build } from 'esbuild';
import { readFileSync, writeFileSync } from 'node:fs';
import postcss from 'postcss';
import tailwindcss from '@tailwindcss/postcss';
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
  alias: { '@': resolve('src/untitled') },
  define: { 'process.env.NODE_ENV': '"production"' },
  legalComments: 'inline',
  banner: { js: '/*!\n' + readFileSync('UNTITLED-UI-LICENSE', 'utf8') + '\n*/' }
});

const css = await postcss([tailwindcss()]).process(readFileSync('src/poc.css', 'utf8'), { from: resolve('src/poc.css'), to: resolve(process.argv[2], 'poc.css') });
writeFileSync(resolve(process.argv[2], 'poc.css'), css.css);
