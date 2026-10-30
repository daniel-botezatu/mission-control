import { spawnSync } from 'node:child_process';
import { chmodSync, copyFileSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';
import { buildConfiguration } from './build-config.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
process.chdir(root);
const args = process.argv.slice(2);
const archFlag = args.indexOf('--arch');
const arch = archFlag < 0 ? process.arch : args[archFlag + 1];
const plan = buildConfiguration(process.platform, arch);
const { windows, rid, triple, extension, components, integrationFiles, bundle } = plan;
if (args.includes('--plan')) { console.log(JSON.stringify(plan, null, 2)); process.exit(0); }
const cargoBin = path.join(process.env.CARGO_HOME || path.join(os.homedir(), '.cargo'), 'bin');
process.env.PATH = cargoBin + path.delimiter + (process.env.PATH || '');
function run(command, values) {
  const result = spawnSync(command, values, { stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status || 1);
}
function copyExecutable(source, target) {
  copyFileSync(source, target);
  if (!windows) chmodSync(target, 0o755);
}
run(process.execPath, ['ui/node_modules/typescript/bin/tsc', '--noEmit', '--project', 'ui/tsconfig.json']);
run(process.execPath, ['ui/node_modules/vite/bin/vite.js', 'build', 'ui', '--config', 'ui/vite.config.ts']);
mkdirSync('src-tauri/binaries', { recursive: true });
for (const component of components) {
  component.output = path.join(root, 'artifacts', `${component.project.toLowerCase()}-${rid}`);
  run('dotnet', ['publish', `src/MissionControl.${component.project}/MissionControl.${component.project}.csproj`,
    '-c', 'Release', '-r', rid, '--self-contained', 'true', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '--packages', '.local/nuget', '-o', component.output]);
  copyExecutable(path.join(component.output, component.executable), `src-tauri/binaries/${component.name}-${triple}${extension}`);
}
// All sidecars live together in Contents/MacOS and are signed by Tauri on Mac.
// Ship setup source, never packages generated from the builder's own profile.
run(process.execPath, ['ui/node_modules/@tauri-apps/cli/tauri.js', 'build', '--ci', '--target', triple,
  '--config', JSON.stringify({ bundle }), ...(args.includes('--no-bundle') ? ['--no-bundle'] : ['--bundles', windows ? 'nsis' : 'app,dmg'])]);
const release = path.join(root, 'src-tauri', 'target', triple, 'release');
for (const component of components) copyExecutable(path.join(component.output, component.executable), path.join(release, component.executable));
for (const file of integrationFiles) {
  const destination = path.join(release, 'integrations', file);
  mkdirSync(path.dirname(destination), { recursive: true });
  copyFileSync(path.join(root, 'integrations', file), destination);
}
console.log(`Desktop build: ${release}`);
