'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { ArrowLeft, ArrowRight, BookOpen, Code2, List, Menu, SquareTerminal, Terminal, X } from 'lucide-react';
import { pages } from '../site/config.mjs';
import { DocsSearch } from './docs-search';

const navigation = ['Start', 'Configure', 'Reference', 'Development'].map(label => ({ label, pages: pages.filter(page => page.group === label) }));
const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? '';

export function DocsShell({ children }: { children: ReactNode }) {
  const rawPath = usePathname();
  const pathname = (basePath && (rawPath === basePath || rawPath.startsWith(`${basePath}/`)) ? rawPath.slice(basePath.length) : rawPath).replace(/\/$/, '') || '/';
  const [open, setOpen] = useState(false);
  const menu = useRef<HTMLButtonElement>(null);
  const sidebar = useRef<HTMLElement>(null);
  const [headings, setHeadings] = useState<{ id: string; title: string; nested: boolean }[]>([]);
  const [activeHeading, setActiveHeading] = useState('');
  const index = pages.findIndex(page => (page.route.replace(/\/$/, '') || '/') === pathname);
  const previous = pages[index - 1];
  const next = pages[index + 1];
  const group = navigation.find(group => group.pages.some(page => page === pages[index]));
  const close = () => setOpen(false);

  useEffect(() => {
    const elements = [...document.querySelectorAll<HTMLElement>('.prose h2[id], .prose h3[id]')];
    setHeadings(elements.map(element => ({ id: element.id, title: element.textContent ?? '', nested: element.tagName === 'H3' })));
    setActiveHeading(elements[0]?.id ?? '');
    const observer = new IntersectionObserver(entries => {
      const visible = entries.find(entry => entry.isIntersecting);
      if (visible) setActiveHeading(visible.target.id);
    }, { rootMargin: '-140px 0px -60% 0px' });
    elements.forEach(element => observer.observe(element));
    return () => observer.disconnect();
  }, [pathname]);

  useEffect(() => {
    if (!open) return;
    const overflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    sidebar.current?.querySelector<HTMLAnchorElement>('a')?.focus();
    const escape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { setOpen(false); menu.current?.focus(); }
    };
    document.addEventListener('keydown', escape);
    return () => { document.body.style.overflow = overflow; document.removeEventListener('keydown', escape); };
  }, [open]);

  return <>
    <a className="skip-link" href="#content">Skip to content</a>
    <header className="site-header">
      <div className="header-main">
        <Link href="/" className="wordmark" onClick={close}><span className="wordmark-mark"><SquareTerminal size={22} aria-hidden="true" /></span><span>CodeAssist</span><span className="wordmark-label">docs</span></Link>
        <DocsSearch onNavigate={close} />
        <Link className="header-quick-start" href="/guide/getting-started/" onClick={close}>First steps <ArrowRight size={15} aria-hidden="true" /></Link>
        <button ref={menu} className="menu-button" type="button" onClick={() => setOpen(value => !value)} aria-expanded={open} aria-controls="docs-navigation" aria-label={open ? 'Close navigation' : 'Open navigation'} title={open ? 'Close navigation' : 'Open navigation'}>{open ? <X size={22} /> : <Menu size={22} />}</button>
      </div>
      <nav className="header-tabs" aria-label="Top navigation">
        <Link href="/" onClick={close} aria-current={group?.label === 'Start' || group?.label === 'Configure' ? 'page' : undefined}><BookOpen size={16} aria-hidden="true" />Guides</Link>
        <Link href="/reference/cli/" onClick={close} aria-current={group?.label === 'Reference' ? 'page' : undefined}><Terminal size={16} aria-hidden="true" />Reference</Link>
        <Link href="/development/architecture/" onClick={close} aria-current={group?.label === 'Development' ? 'page' : undefined}><Code2 size={16} aria-hidden="true" />Development</Link>
      </nav>
    </header>
    <div className="docs-layout">
      {open && <button className="navigation-backdrop" type="button" onClick={close} aria-label="Close navigation" />}
      <aside ref={sidebar} id="docs-navigation" className={`sidebar${open ? ' is-open' : ''}`} aria-label="Documentation navigation">
        <div className="navigation-groups">{navigation.map(group => <section key={group.label}><h2>{group.label}</h2><nav aria-label={group.label}>{group.pages.map(page => <Link key={page.route} href={page.route} onClick={close} aria-current={page === pages[index] ? 'page' : undefined}>{page.title}</Link>)}</nav></section>)}</div>
        <div className="sidebar-note"><span className="status-dot" />CLI-first / .NET 10</div>
      </aside>
      <main id="content" tabIndex={-1} className="docs-main">
        <div className="page-context"><span>{group?.label ?? 'Documentation'}</span><span>Source build</span></div>
        <article className="prose" data-pagefind-body>{children}</article>
        <nav className="page-pagination" aria-label="Page navigation">
          {previous ? <Link href={previous.route}><ArrowLeft size={16} aria-hidden="true" /><span><small>Previous</small>{previous.title}</span></Link> : <span />}
          {next && <Link href={next.route}><span><small>Next</small>{next.title}</span><ArrowRight size={16} aria-hidden="true" /></Link>}
        </nav>
        <footer className="page-footer">CodeAssist <span>Contracts first. Runtime in progress.</span></footer>
      </main>
      {headings.length > 0 && <aside className="page-outline" aria-label="On this page"><h2><List size={15} aria-hidden="true" />On this page</h2><nav>{headings.map(heading => <a key={heading.id} className={heading.nested ? 'is-nested' : undefined} href={`#${heading.id}`} aria-current={activeHeading === heading.id ? 'location' : undefined} onClick={() => setActiveHeading(heading.id)}>{heading.title}</a>)}</nav></aside>}
    </div>
  </>;
}