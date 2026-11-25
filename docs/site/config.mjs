export function getBasePath(environment = process.env) {
  const configured = environment.DOCS_BASE_PATH;
  const repository = environment.GITHUB_REPOSITORY?.split('/')[1];
  const value = configured ?? (environment.GITHUB_ACTIONS === 'true' && repository && !repository.endsWith('.github.io') ? `/${repository}` : '');
  if (value !== '' && (!value.startsWith('/') || value.includes('..') || value.includes('?') || value.includes('#'))) {
    throw new Error('DOCS_BASE_PATH must be empty or an absolute URL path without traversal, query, or fragment.');
  }
  return value.replace(/\/+$/, '');
}

export const pages = [
  { source: 'content/index.md', route: '/', title: 'Overview', group: 'Start', description: 'CodeAssist commands, capabilities and current delivery boundaries.' },
  { source: 'content/guide/getting-started.md', route: '/guide/getting-started/', title: 'First steps', group: 'Start', description: 'Build the CLI and inspect your first configuration.' },
  { source: 'content/guide/features.md', route: '/guide/features/', title: 'Feature status', group: 'Start', description: 'Implemented components and work that is still pending.' },
  { source: 'content/guide/configuration.md', route: '/guide/configuration/', title: 'Configuration', group: 'Configure', description: 'Trust tiers, environment overrides, secrets and provenance.' },
  { source: 'content/guide/providers.md', route: '/guide/providers/', title: 'Providers', group: 'Configure', description: 'Provider presets, endpoint routing and environment credentials.' },
  { source: 'content/guide/models.md', route: '/guide/models/', title: 'Models and catalog', group: 'Configure', description: 'Refresh models.dev metadata and understand model capabilities.' },
  { source: 'content/guide/connectors.md', route: '/guide/connectors/', title: 'Connectors', group: 'Configure', description: 'Manage trusted external tool-server definitions.' },
  { source: 'content/guide/plugins.md', route: '/guide/plugins/', title: 'Plugins', group: 'Configure', description: 'Create, inspect and digest-approve declarative plugin packages.' },
  { source: 'content/guide/skills.md', route: '/guide/skills/', title: 'Skills', group: 'Configure', description: 'Scoped guidance, structured invocation and authorized execution recipes.' },
  { source: 'content/guide/tools.md', route: '/guide/tools/', title: 'Tools', group: 'Reference', description: 'Tool schemas, risk, effects and component boundaries.' },
  { source: 'content/guide/server.md', route: '/guide/server/', title: 'Agent server', group: 'Reference', description: 'Authenticated local HTTP sessions, streaming and delivery boundaries.' },
  { source: 'content/guide/vscode.md', route: '/guide/vscode/', title: 'VS Code app', group: 'Start', description: 'Install the native editor client and connect to the shared .NET runtime.' },
  { source: 'content/reference/cli.md', route: '/reference/cli/', title: 'CLI reference', group: 'Reference', description: 'Available commands, options and source-checkout invocation.' },
  { source: 'content/reference/troubleshooting.md', route: '/reference/troubleshooting/', title: 'Troubleshooting', group: 'Reference', description: 'Diagnose provenance, credentials, empty catalogs and missing commands.' },
  { source: 'content/development/architecture.md', route: '/development/architecture/', title: 'CodeAssist Architecture', group: 'Development', description: 'Package-based target design, shared C# runtime, client diagrams and current implementation boundaries.' },
  { source: 'content/development/testing.md', route: '/development/testing/', title: 'Testing', group: 'Development', description: 'Owner-aligned unit/integration suites and opt-in live checks.' },
  { source: 'content/development/contributing.md', route: '/development/contributing/', title: 'Contributing', group: 'Development', description: 'Place changes, verify contracts and document delivery honestly.' }
];