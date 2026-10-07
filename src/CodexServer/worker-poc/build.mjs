import { build } from 'esbuild';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, mkdirSync, readdirSync } from 'node:fs';
import postcss from 'postcss';
import tailwindcss from '@tailwindcss/postcss';
import { resolve } from 'node:path';
if (!process.argv[2]) throw new Error('An output directory is required.');
mkdirSync(resolve(process.argv[2]), { recursive: true });
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

// Vite owns the production SPA graph, including hashed JS/CSS and imported assets.
const preview = resolve(process.argv[2], 'preview');
// Tailwind caches its input graph in-process. Isolate Vite from the retained
// PoC PostCSS build so neither compilation can return the other's empty delta.
execFileSync(process.execPath, ['node_modules/vite/bin/vite.js', 'build', '--outDir', preview, '--emptyOutDir'], {
  stdio: 'inherit', timeout: 120000
});
const assets = [];
function collect(directory, prefix = '') {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const name = prefix + entry.name;
    if (entry.isDirectory()) collect(resolve(directory, entry.name), name + '/');
    else if (name.startsWith('assets/')) {
      const bytes = readFileSync(resolve(preview, name));
      if (!bytes.length) throw new Error(`Empty dashboard asset: ${name}`);
      assets.push({ path: name, sha256: createHash('sha256').update(bytes).digest('hex') });
    }
  }
}
collect(preview);
if (!assets.some(asset => asset.path.endsWith('.js')) || !assets.some(asset => asset.path.endsWith('.css'))) throw new Error('Dashboard JS/CSS missing.');
writeFileSync(resolve(preview, 'assets.json'), JSON.stringify(assets));
