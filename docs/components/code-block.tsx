'use client';

import { useRef, useState, type ComponentProps } from 'react';
import { Check, Copy } from 'lucide-react';

export function CodeBlock(props: ComponentProps<'pre'>) {
  const content = useRef<HTMLPreElement>(null);
  const [status, setStatus] = useState('Copy code');
  async function copy() {
    try {
      await navigator.clipboard.writeText(content.current?.innerText ?? '');
      setStatus('Copied');
    } catch { setStatus('Copy failed; select code manually'); }
  }
  return <div className="code-block">
    <pre {...props} ref={content} />
    <button className="copy-button" type="button" onClick={copy} title={status} aria-label={status}>
      {status === 'Copied' ? <Check size={15} aria-hidden="true" /> : <Copy size={15} aria-hidden="true" />}
    </button>
    <span className="sr-only" role="status">{status === 'Copy code' ? '' : status}</span>
  </div>;
}