'use strict';

// Baut ein eigenständiges, installierbares Paket nach Legacy/dist/adapter-core
// (NodeAdapterClient.js wird unverändert nach vendor/ kopiert) und optional `npm pack`.
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const source = __dirname;
const target = path.resolve(source, '..', 'dist', 'adapter-core');
const clientFile = path.resolve(source, '..', 'NodeAdapterClient.js');

fs.rmSync(target, { recursive: true, force: true });
fs.mkdirSync(path.join(target, 'vendor'), { recursive: true });

const files = [
  'index.js', 'Adapter.js', 'StateApi.js', 'ObjectApi.js', 'MessageApi.js', 'LoggerApi.js',
  'SubscriptionManager.js', 'BridgeStateSynchronizer.js', 'util.js', 'package.json', 'README.md'
];
for (const file of files) fs.copyFileSync(path.join(source, file), path.join(target, file));
fs.copyFileSync(clientFile, path.join(target, 'vendor', 'NodeAdapterClient.js'));

if (process.argv.includes('--pack')) {
  const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm';
  const output = execFileSync(npm, ['pack', '--silent'], { cwd: target, shell: process.platform === 'win32' }).toString().trim();
  console.log(`packed: ${path.join(target, output.split(/\r?\n/).pop())}`);
} else {
  console.log(`built: ${target}`);
}
