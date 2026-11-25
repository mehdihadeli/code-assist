'use client';

import { useId, useState } from 'react';
import { Check, Copy, Terminal } from 'lucide-react';

const snapshots = [
  {
    title: 'Commands',
    command: 'code-assist --help',
    output: 'USAGE:\n    code-assist [OPTIONS] <COMMAND>\n\nCOMMANDS:\n    config        Inspect and change settings\n    providers     Manage model providers\n    tools         Inspect the tools the agent can use\n    connectors    Manage external tool servers\n    doctor        Check settings, providers and connectors'
  },
  {
    title: 'Provenance',
    command: 'code-assist config get agent.maxTurns --origin',
    output: '20\nOnly the built-in default applies.'
  }
];

export function CommandPreview() {
  const id = useId();
  const [selected, setSelected] = useState(0);
  const [copyStatus, setCopyStatus] = useState('Copy command');
  const snapshot = snapshots[selected];

  function select(index: number) {
    setSelected(index);
    setCopyStatus('Copy command');
  }

  async function copy() {
    try { await navigator.clipboard.writeText(snapshot.command); setCopyStatus('Copied'); }
    catch { setCopyStatus('Copy failed; select command manually'); }
  }

  return <figure className="command-preview" aria-label="CodeAssist CLI output examples">
    <figcaption><Terminal size={15} aria-hidden="true" /><span>code-assist</span><small>CLI snapshot</small></figcaption>
    <div className="command-tabs" role="tablist" aria-label="CLI examples" onKeyDown={event => {
      const current = snapshots.findIndex((_, index) => event.target === event.currentTarget.children[index]);
      if (current < 0) return;
      const target = event.key === 'ArrowRight' ? (current + 1) % snapshots.length : event.key === 'ArrowLeft' ? (current - 1 + snapshots.length) % snapshots.length : event.key === 'Home' ? 0 : event.key === 'End' ? snapshots.length - 1 : -1;
      if (target < 0) return;
      event.preventDefault();
      select(target);
      (event.currentTarget.children[target] as HTMLButtonElement)?.focus();
    }}>
      {snapshots.map((item, index) => <button key={item.title} id={`${id}-tab-${index}`} role="tab" type="button" aria-selected={selected === index} aria-controls={`${id}-panel-${index}`} tabIndex={selected === index ? 0 : -1} onClick={() => select(index)}>{item.title}</button>)}
    </div>
    {snapshots.map((item, index) => <div key={item.title} className="command-panel" id={`${id}-panel-${index}`} role="tabpanel" aria-labelledby={`${id}-tab-${index}`} hidden={selected !== index} tabIndex={0}>
      <div className="command-line"><code><span aria-hidden="true">$ </span>{item.command}</code><button type="button" className="command-copy" aria-label={copyStatus} title={copyStatus} onClick={copy}>{copyStatus === 'Copied' ? <Check size={15} aria-hidden="true" /> : <Copy size={15} aria-hidden="true" />}</button></div>
      <pre><code>{item.output}</code></pre>
    </div>)}
    <div className="command-caption"><span className="status-dot" />Local build / output excerpts</div>
    <span className="sr-only" role="status">{copyStatus === 'Copy command' ? '' : copyStatus}</span>
  </figure>;
}