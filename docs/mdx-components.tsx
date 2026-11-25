import Link from 'next/link';
import type { ComponentProps } from 'react';
import { useMDXComponents as getNextraComponents } from 'nextra/mdx-components';
import type { MDXComponents } from 'mdx/types';
import { CodeBlock } from './components/code-block';
import { CommandPreview } from './components/command-preview';

const defaults = getNextraComponents();

function Anchor({ href = '', ...props }: ComponentProps<'a'>) {
  return <Link href={href} {...props} />;
}

export function useMDXComponents(components: MDXComponents = {}) {
  return { ...defaults, a: Anchor, pre: CodeBlock, CommandPreview, ...components };
}