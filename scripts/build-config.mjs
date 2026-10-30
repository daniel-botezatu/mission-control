const commonIntegrationFiles = [
  'README.md', 'manage.py', 'setup.py',
  'verify_memory_package.py', 'shared/developer-memory.md', 'shared/read-tools.json',
  'shared/recap-reader.md', 'shared/recap-coordinator.md',
];

export function buildConfiguration(platform, arch) {
  if (!['win32', 'darwin'].includes(platform)) throw new Error('Build the desktop on Windows or macOS.');
  if (!['x64', 'arm64'].includes(arch)) throw new Error('Choose x64 or arm64.');
  const windows = platform === 'win32';
  const rid = `${windows ? 'win' : 'osx'}-${arch}`;
  const triple = `${arch === 'arm64' ? 'aarch64' : 'x86_64'}-${windows ? 'pc-windows-msvc' : 'apple-darwin'}`;
  const extension = windows ? '.exe' : '';
  const components = (windows ? ['Backend', 'Mcp', 'Providers'] : ['Backend', 'Mcp']).map(project => ({
    project, name: `mission-control-${project.toLowerCase()}`,
    executable: `mission-control-${project.toLowerCase()}${extension}`,
  }));
  const integrationFiles = [...commonIntegrationFiles, ...(windows ? ['configure_github_provider.py'] : [])];
  const resources = Object.fromEntries(integrationFiles.map(file => [`../integrations/${file}`, `integrations/${file}`]));
  const bundle = { resources, externalBin: components.map(c => `binaries/${c.name}`) };
  if (!windows) bundle.macOS = { minimumSystemVersion: '15.0', entitlements: 'Entitlements.plist' };
  return { windows, rid, triple, extension, components, integrationFiles, bundle };
}
