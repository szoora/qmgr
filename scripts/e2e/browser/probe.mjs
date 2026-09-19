// A scratch probe: open each route given on the command line and print what its page header is
// made of, child by child, with heights. Used to find which part of a band is spending the space.
//
//   node scripts/e2e/browser/probe.mjs /portal /admin/students/roster
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
const routes = process.argv.slice(2);
const t = await openTab();
await t.viewport(1500, 950);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');

for (const r of routes) {
  await t.goto(BASE + r);
  await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
  await t.sleep(3000);
  const out = await t.eval(`(() => {
    const h = document.querySelector('.qm-main .page-header');
    if (!h) return JSON.stringify({ header: null });
    const cs = getComputedStyle(h);
    return JSON.stringify({
      total: Math.round(h.getBoundingClientRect().height),
      padB: cs.paddingBottom, marB: cs.marginBottom, display: cs.display,
      hcStyle: (() => { const c = h.querySelector(".header-content"); if (!c) return null; const s = getComputedStyle(c); return { display: s.display, align: s.alignItems, wrap: s.flexWrap, h: Math.round(c.getBoundingClientRect().height) }; })(),
      kids: [...h.children].map(c => ({
        cls: c.className,
        h: Math.round(c.getBoundingClientRect().height),
        text: c.innerText.slice(0, 44).split('\\n').join(' | ')
      }))
    });
  })()`);
  console.log(r + '  ' + out);
}
await t.close();
