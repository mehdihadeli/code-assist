'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useEffect, useRef, useState } from 'react';
import { Search } from 'lucide-react';
import { pages } from '../site/config.mjs';

type Result = { url: string; title: string; excerpt?: string };
type Pagefind = { search: (query: string) => Promise<{ results: { data: () => Promise<{ url: string; meta: { title: string }; excerpt: string }> }[] }> };
const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? '';

export function DocsSearch({ onNavigate }: { onNavigate: () => void }) {
  const pathname = usePathname();
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<Result[]>([]);
  const [loading, setLoading] = useState(false);
  const container = useRef<HTMLDivElement>(null);

  useEffect(() => setQuery(''), [pathname]);
  useEffect(() => {
    const dismiss = (event: PointerEvent) => {
      if (!container.current?.contains(event.target as Node)) setQuery('');
    };
    document.addEventListener('pointerdown', dismiss);
    return () => document.removeEventListener('pointerdown', dismiss);
  }, []);

  useEffect(() => {
    let cancelled = false;
    const term = query.trim();
    if (!term) { setResults([]); setLoading(false); return; }
    setResults([]);
    setLoading(true);
    const timer = setTimeout(async () => {
      try {
        const modulePath = `${basePath}/_pagefind/pagefind.js`;
        const pagefind: Pagefind = await import(/* webpackIgnore: true */ modulePath);
        const search = await pagefind.search(term);
        const found = await Promise.all(search.results.slice(0, 8).map(async result => {
          const data = await result.data();
          const url = new URL(data.url, window.location.origin);
          const route = basePath && url.pathname.startsWith(`${basePath}/`) ? url.pathname.slice(basePath.length) : url.pathname;
          return { url: `${route}${url.hash}`, title: data.meta.title, excerpt: new DOMParser().parseFromString(data.excerpt, 'text/html').body.textContent ?? '' };
        }));
        if (!cancelled) setResults(found);
      } catch {
        if (!cancelled) setResults(pages.filter(page => `${page.title} ${page.description} ${page.group}`.toLowerCase().includes(term.toLowerCase())).map(page => ({ url: page.route, title: page.title, excerpt: page.description })));
      } finally { if (!cancelled) setLoading(false); }
    }, 150);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [query]);

  return <div className="header-search" ref={container}>
    <label className="search-field"><Search size={16} aria-hidden="true" /><input value={query} onChange={event => setQuery(event.target.value)} onKeyDown={event => { if (event.key === 'Escape') setQuery(''); if (event.key === 'ArrowDown') { event.preventDefault(); container.current?.querySelector<HTMLAnchorElement>('.search-results a')?.focus(); } }} placeholder="Search CodeAssist..." aria-label="Search documentation" aria-controls={query.trim() ? 'search-results' : undefined} type="search" /></label>
    {query.trim() && <nav id="search-results" className="search-results" aria-label="Search results" aria-busy={loading}>
      {results.map(result => <Link key={result.url} href={result.url} onClick={() => { setQuery(''); onNavigate(); }}><span>{result.title}</span>{result.excerpt && <small>{result.excerpt}</small>}</Link>)}
      <div role="status">{loading ? <p className="no-results">Searching...</p> : !results.length && <p className="no-results">No pages found.</p>}</div>
    </nav>}
  </div>;
}