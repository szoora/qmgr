// A live results viewer for the e2e suites, so a run can be WATCHED rather than read afterwards.
//
//   node scripts/e2e/browser/viewer.mjs            then open http://127.0.0.1:5010
//
// The browser checks already POST their PASS/FAIL lines to /append?key=ui (see select-verify.mjs).
// The curl and Node suites print to stdout, so pipe them through tee-to-viewer.mjs:
//
//   bash scripts/e2e/class-teacher-e2e.sh | node scripts/e2e/browser/tee-to-viewer.mjs api
//
// Nothing here is part of the app; it is a test harness that binds to loopback only.
import { createServer } from 'node:http';

const PORT = 5010;
/** @type {{ key: string, text: string, at: number }[]} */
const lines = [];
let cleared = 0;

const PAGE = `<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>SACC Dashboard e2e</title>
<style>
  :root {
    --bg: #14100f; --card: #1d1817; --line: #2e2725; --text: #f3efee; --muted: #a79f9c;
    --pass: #4ea87b; --fail: #d9534f; --skip: #c9a227; --wine: #8c2f52;
    --mono: ui-monospace, "Cascadia Mono", Consolas, monospace;
  }
  @media (prefers-color-scheme: light) {
    :root:not([data-theme="dark"]) {
      --bg: #f7f5f4; --card: #fff; --line: #e4dedc; --text: #1c1918; --muted: #6d6664;
      --wine: #7a2847;
    }
  }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--text);
         font: 14px/1.5 Poppins, system-ui, sans-serif; }
  header { position: sticky; top: 0; z-index: 2; background: var(--card);
           border-bottom: 1px solid var(--line); padding: 12px 16px;
           display: flex; align-items: center; gap: 16px; flex-wrap: wrap; }
  h1 { font: 700 18px/1.2 Montserrat, system-ui, sans-serif; margin: 0; }
  .counts { display: flex; gap: 8px; flex-wrap: wrap; margin-left: auto; }
  .pill { border-radius: 3px; padding: 3px 10px; font-variant-numeric: tabular-nums;
          font-weight: 600; font-size: 13px; border: 1px solid var(--line); }
  .pill.pass { color: var(--pass); border-color: color-mix(in srgb, var(--pass) 45%, transparent); }
  .pill.fail { color: var(--fail); border-color: color-mix(in srgb, var(--fail) 45%, transparent); }
  .pill.skip { color: var(--skip); border-color: color-mix(in srgb, var(--skip) 45%, transparent); }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--pass);
         box-shadow: 0 0 0 3px color-mix(in srgb, var(--pass) 25%, transparent); }
  .dot.idle { background: var(--muted); box-shadow: none; }
  main { padding: 16px; max-width: 1100px; margin: 0 auto; }
  .stream { background: var(--card); border: 1px solid var(--line); border-radius: 6px;
            overflow: hidden; }
  .row { display: grid; grid-template-columns: 74px 1fr; gap: 10px; padding: 4px 12px;
         border-top: 1px solid color-mix(in srgb, var(--line) 55%, transparent);
         font-family: var(--mono); font-size: 12.5px; white-space: pre-wrap;
         overflow-wrap: anywhere; }
  .row:first-child { border-top: 0; }
  .row .tag { color: var(--muted); text-transform: uppercase; font-size: 11px;
              letter-spacing: .04em; padding-top: 2px; }
  .row.pass .txt { color: var(--pass); }
  .row.fail .txt { color: var(--fail); font-weight: 600;
                   background: color-mix(in srgb, var(--fail) 12%, transparent); }
  .row.skip .txt { color: var(--skip); }
  .row.head .txt { color: var(--text); font-weight: 700; font-family: Montserrat, sans-serif;
                   font-size: 13px; padding-top: 10px; }
  .empty { color: var(--muted); padding: 40px 16px; text-align: center; }
  button { font: inherit; font-size: 13px; min-height: 30px; padding: 0 12px;
           border-radius: 4px; border: 1px solid var(--line); background: transparent;
           color: var(--text); cursor: pointer; }
  button.on { background: var(--wine); border-color: var(--wine); color: #fff; }
  @media (max-width: 640px) { .row { grid-template-columns: 56px 1fr; } }
</style></head>
<body>
<header>
  <span class="dot idle" id="dot"></span>
  <h1>SACC Dashboard e2e</h1>
  <button id="follow" class="on">Follow</button>
  <button id="only">Failures only</button>
  <div class="counts">
    <span class="pill pass" id="cPass">0 passed</span>
    <span class="pill fail" id="cFail">0 failed</span>
    <span class="pill skip" id="cSkip">0 skipped</span>
    <span class="pill" id="cAll">0 lines</span>
  </div>
</header>
<main><div class="stream" id="stream"><div class="empty">Waiting for a run…</div></div></main>
<script>
  let since = 0, follow = true, failOnly = false, lastAt = 0;
  const stream = document.getElementById('stream');
  const cls = (t) => {
    if (/\\bFAIL\\b|✗|✖/.test(t)) return 'fail';
    if (/\\bPASS\\b|✓/.test(t)) return 'pass';
    if (/\\bSKIP\\b/i.test(t)) return 'skip';
    if (/^\\s*(={2,}|-{2,}|#|Section|\\[[0-9.]+\\])/.test(t)) return 'head';
    return '';
  };
  function render(rows) {
    if (!rows.length) return;
    if (stream.querySelector('.empty')) stream.textContent = '';
    for (const r of rows) {
      const k = cls(r.text);
      if (failOnly && k !== 'fail' && k !== 'head') continue;
      const el = document.createElement('div');
      el.className = 'row ' + k;
      el.innerHTML = '<span class="tag"></span><span class="txt"></span>';
      el.querySelector('.tag').textContent = r.key;
      el.querySelector('.txt').textContent = r.text;
      stream.appendChild(el);
    }
    if (follow) window.scrollTo(0, document.body.scrollHeight);
  }
  async function poll() {
    try {
      const res = await fetch('/lines?since=' + since);
      const d = await res.json();
      if (d.reset) { since = 0; stream.textContent = ''; }
      since = d.next;
      render(d.lines);
      document.getElementById('cPass').textContent = d.pass + ' passed';
      document.getElementById('cFail').textContent = d.fail + ' failed';
      document.getElementById('cSkip').textContent = d.skip + ' skipped';
      document.getElementById('cAll').textContent = d.total + ' lines';
      const live = d.lastAt && Date.now() - d.lastAt < 6000;
      document.getElementById('dot').className = 'dot' + (live ? '' : ' idle');
      document.title = (d.fail ? '✗ ' + d.fail + ' — ' : '') + 'SACC Dashboard e2e';
    } catch {}
    setTimeout(poll, 500);
  }
  document.getElementById('follow').onclick = (e) => {
    follow = !follow; e.target.classList.toggle('on', follow);
  };
  document.getElementById('only').onclick = (e) => {
    failOnly = !failOnly; e.target.classList.toggle('on', failOnly);
    since = 0; stream.textContent = '';
  };
  poll();
</script>
</body></html>`;

const count = (re) => lines.filter(l => re.test(l.text)).length;

createServer((req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  if (req.method === 'POST' && url.pathname === '/append') {
    const key = (url.searchParams.get('key') || 'run').slice(0, 12);
    let body = '';
    req.on('data', c => { body += c; if (body.length > 1e6) req.destroy(); });
    req.on('end', () => {
      for (const text of body.split('\n')) {
        if (text.trim()) lines.push({ key, text: text.replace(/\x1b\[[0-9;]*m/g, ''), at: Date.now() });
      }
      res.writeHead(204).end();
    });
    return;
  }
  if (req.method === 'POST' && url.pathname === '/clear') {
    lines.length = 0; cleared++;
    res.writeHead(204).end();
    return;
  }
  if (url.pathname === '/lines') {
    const since = Number(url.searchParams.get('since') || 0);
    const from = since > lines.length ? 0 : since;
    res.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' });
    res.end(JSON.stringify({
      lines: lines.slice(from), next: lines.length, reset: from === 0 && since > 0,
      total: lines.length,
      pass: count(/\bPASS\b|✓/), fail: count(/\bFAIL\b|✗|✖/), skip: count(/\bSKIP\b/i),
      lastAt: lines.length ? lines[lines.length - 1].at : 0,
    }));
    return;
  }
  res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
  res.end(PAGE);
}).listen(PORT, '127.0.0.1', () => console.log(`e2e viewer on http://127.0.0.1:${PORT}`));
