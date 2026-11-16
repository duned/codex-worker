import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, mkdirSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
if (!process.argv[2]) throw new Error('An output directory is required.');
mkdirSync(resolve(process.argv[2]), { recursive: true });
// Vite owns the production SPA graph, including hashed JS/CSS and imported assets.
const preview = resolve(process.argv[2], 'preview');
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
