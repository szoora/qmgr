// Measures buttons, button-row gaps and headings on every parameterless page. Writes ui-audit.json.
import fs from 'node:fs';
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

// Every parameterless @page route in the Web project, read from the .razor files.
const require_routes = () => {
  const components = fileURLToPath(new URL('../../../src/Q-Mgr.Web/Components', import.meta.url));
  const walk = (d) => fs.readdirSync(d, { withFileTypes: true })
    .flatMap((e) => e.isDirectory() ? walk(path.join(d, e.name)) : e.name.endsWith('.razor') ? [path.join(d, e.name)] : []);
  const routes = walk(components).flatMap((f) => [...fs.readFileSync(f, 'utf8').matchAll(/@page "([^"{]*)"/g)].map((m) => m[1]));
  return [...new Set(routes)].sort().join('\n');
};
const BASE = 'http://127.0.0.1:5003';
const skip = /^\/(login|register|forgot-password|reset-password|verify|join|s\/|kiosk|display|feedback\/|customer|queue-status|check-in|visitor-kiosk|docs|privacy|terms|unauthorized|logout|error|not-found|$)/i;
const routes = require_routes().split(/\r?\n/).filter(Boolean).filter((r) => !skip.test(r) && r !== '/');
const who = process.env.WHO ?? 'admin';
const t = await openTab();
await t.viewport(1440, 900);
if (who === 'sa') await login(t, 'superadmin', 'admin'); else await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');
const probe = `(() => {
  const vis = (e) => { const r = e.getBoundingClientRect(); const cs = getComputedStyle(e); return r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none'; };
  const main = document.querySelector('.qm-main') ?? document.body;
  const isBtn = (e) => e.matches('button, .q-btn, a.btn, [role=button]') && !e.closest('.qm-sidebar, .qm-header, .q-select__dropdown, .q-datepicker__popover, .stu-tab, .q-tabs, .q-pager, table');
  const btns = [...main.querySelectorAll('button, .q-btn, a.btn, [role=button]')].filter((e) => isBtn(e) && vis(e) && !e.parentElement.closest('button, .q-btn'));
  const one = (e) => { const cs = getComputedStyle(e); const r = e.getBoundingClientRect();
    const kind = e.classList.contains('q-btn') ? 'QButton ' + ([...e.classList].find((c) => /^q-btn--(sm|md|lg)$/.test(c)) ?? '') + (e.closest('.header-actions') ? ' (header)' : '') : 'raw .' + ([...e.classList].filter((c) => !/active|selected|disabled/.test(c))[0] ?? e.tagName.toLowerCase());
    return { kind, text: e.innerText.trim().slice(0, 30), font: cs.fontFamily.split(',')[0].replace(/["']/g, ''), size: cs.fontSize, weight: cs.fontWeight, h: Math.round(r.height), padX: cs.paddingLeft, radius: cs.borderRadius, iconOnly: !e.innerText.trim() }; };
  const rows = [];
  const parents = new Set(btns.map((b) => b.parentElement));
  for (const p of parents) {
    const kids = [...p.children].filter((c) => btns.includes(c));
    if (kids.length < 2) continue;
    const gaps = [];
    for (let i = 1; i < kids.length; i++) { const a = kids[i - 1].getBoundingClientRect(), b = kids[i].getBoundingClientRect();
      if (Math.abs(a.top - b.top) < 6 && b.left >= a.right - 1) gaps.push(Math.round(b.left - a.right)); }
    if (gaps.length) rows.push({ row: '.' + ([...p.classList][0] ?? p.tagName.toLowerCase()), gaps, texts: kids.map((k) => k.innerText.trim().slice(0, 16)) });
  }
  const h1 = document.querySelector('.qm-main h1');
  const hcs = h1 && getComputedStyle(h1);
  const pcs = getComputedStyle(main.querySelector('p') ?? main);
  return { url: location.pathname, buttons: btns.map(one), rows, h1: h1 ? { text: h1.innerText.slice(0, 30), size: hcs.fontSize, font: hcs.fontFamily.split(',')[0].replace(/["']/g, ''), weight: hcs.fontWeight } : null,
    body: { font: pcs.fontFamily.split(',')[0].replace(/["']/g, ''), size: pcs.fontSize } };
})()`;
const out = [];
for (const r of routes) {
  try {
    await t.goto(BASE + r);
    await t.waitFor(`!!document.querySelector('.qm-main h1, .qm-main .page-header') || location.pathname !== ${JSON.stringify(r)}`, 12000);
    await t.sleep(2200);
    const res = await t.eval(probe);
    res.route = r;
    if (res.url !== r) res.redirected = true;
    out.push(res);
    process.stdout.write(`${r}${res.redirected ? ' -> ' + res.url : ''}: ${res.buttons.length} buttons, ${res.rows.length} rows\n`);
  } catch (e) { process.stdout.write(`${r}: ERROR ${e.message.slice(0, 80)}\n`); }
}
fs.writeFileSync(`ui-audit-${who}.json`, JSON.stringify(out, null, 1));
t.close();
