import type { Metadata } from 'next';
import type { ReactNode } from 'react';
import { DocsShell } from '../components/docs-shell';
import '@fontsource-variable/geist';
import '@fontsource/ibm-plex-mono/400.css';
import '@fontsource/ibm-plex-mono/500.css';
import './globals.css';

export const metadata: Metadata = {
  title: { default: 'CodeAssist Documentation', template: '%s | CodeAssist' },
  description: 'Build and configure CodeAssist: layered settings, model providers, tools, connector definitions, and the .NET development workflow.'
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en" dir="ltr" data-scroll-behavior="smooth">
      <body><DocsShell>{children}</DocsShell></body>
    </html>
  );
}