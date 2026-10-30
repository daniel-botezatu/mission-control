import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { buildConfiguration } from './build-config.mjs';

test('Apple Silicon and Intel builds stage all executable sidecars and complete setup sources', () => {
  for (const [arch, triple] of [['arm64', 'aarch64-apple-darwin'], ['x64', 'x86_64-apple-darwin']]) {
    const plan = buildConfiguration('darwin', arch);
    assert.equal(plan.triple, triple);
    assert.equal(plan.rid, `osx-${arch}`);
    assert.deepEqual(plan.bundle.externalBin, ['binaries/mission-control-backend', 'binaries/mission-control-mcp']);
    assert.deepEqual(plan.components.map(c => c.project), ['Backend', 'Mcp']);
    assert.ok(!plan.integrationFiles.includes('configure_github_provider.py'));
    assert.ok(!JSON.stringify(plan.bundle.resources).includes('configure_github_provider.py'));
    assert.equal(plan.bundle.macOS.minimumSystemVersion, '15.0');
    assert.ok(plan.integrationFiles.includes('shared/recap-coordinator.md'));
    assert.ok(plan.integrationFiles.includes('shared/recap-reader.md'));
    for (const file of plan.integrationFiles) assert.ok(existsSync(new URL(`../integrations/${file}`, import.meta.url)), file);
    assert.ok(!JSON.stringify(plan).includes('C:\\Users'));
    assert.ok(plan.components.every(c => !c.executable.endsWith('.exe')));
  }
});
test('Windows still stages executable sidecars with .exe extensions', () => {
  const plan = buildConfiguration('win32', 'x64');
  assert.equal(plan.triple, 'x86_64-pc-windows-msvc');
  assert.deepEqual(plan.components.map(c => c.project), ['Backend', 'Mcp', 'Providers']);
  assert.ok(plan.integrationFiles.includes('configure_github_provider.py'));
  assert.ok(plan.bundle.externalBin.includes('binaries/mission-control-providers'));
  assert.ok(plan.components.every(c => c.executable.endsWith('.exe')));
  assert.equal(plan.bundle.macOS, undefined);
});
test('unsupported architectures fail instead of silently choosing x64', () => {
  assert.throws(() => buildConfiguration('darwin', 'ia32'));
});
test('release packaging does not generate plugins containing the builders profile paths', () => {
  const source = readFileSync(new URL('./package-release.ps1', import.meta.url), 'utf8');
  assert.ok(!source.includes('manage.py\') prepare'));
  assert.ok(!source.includes('LOCALAPPDATA'));
});
