// Spot-measure the pages from the user's screenshots and the rows fixed last, at desktop and phone width.
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';
const BASE = 'http://127.0.0.1:5003';
let pass = 0, fail = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => { ok ? pass++ : fail++; const l = `    ${ok ? 'PASS' : 'FAIL'}  UNIFORM: ${name}${ok ? '' : '  — ' + detail}`; console.log(l); post(l); };
const t = await openTab();
await t.viewport(1440, 900);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');
post('\n== One size scale: controls, rows, titles ==');
const measure = `(() => {
  const vis = (e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  const head = [...document.querySelectorAll('.header-actions')].find(vis);
  const hb = head ? [...head.children].filter((c) => c.matches('.q-btn') && vis(c)) : [];
  const gaps = hb.slice(1).map((b, i) => Math.round(b.getBoundingClientRect().left - hb[i].getBoundingClientRect().right)).filter((g) => g >= 0);
  const heights = [...document.querySelectorAll('.qm-main .q-btn:not(.q-btn--sm):not(.q-btn--lg), .qm-main input.q-input, .qm-main .q-select:not(.q-multiselect), .qm-main .q-datepicker__trigger')].filter(vis).map((e) => Math.round(e.getBoundingClientRect().height));
  const band = document.querySelector('.qm-main .page-header');
  const furniture = band ? Math.round(band.getBoundingClientRect().height + parseFloat(getComputedStyle(band).marginBottom || 0)) : null;
  const h1 = document.querySelector('.qm-main h1'); const cs = h1 && getComputedStyle(h1);
  // WHICH rule actually won the title size, not just what it computed to. The 2026-09-18 title
  // reduction sat in layout.css and was beaten by an equal-specificity .page-header h1 rule in
  // qm-theme.css for a day and a half, so 24px was true in the source and never on the screen.
  let winner = null;
  for (const sheet of document.styleSheets) {
    let rules; try { rules = sheet.cssRules; } catch { continue; }
    for (const r of rules) {
      if (!r.selectorText || !r.style || !r.style.fontSize) continue;
      try { if (h1 && h1.matches(r.selectorText)) winner = r.selectorText + ' @ ' + (sheet.href || 'inline').split('/').pop(); } catch {}
    }
  }
  return { gaps, heights: [...new Set(heights)], h1: cs ? cs.fontSize + ' ' + cs.fontWeight : null, winner, furniture, overflow: document.documentElement.scrollWidth - innerWidth };
})()`;
// Hub routes since 2026-09-18: /admin/timetable/settings, /admin/staff/rota and
// /admin/staff/activity were retired into tabs and 404 now, so the suite measured a page with no
// h1 and reported null three times over.
for (const route of ['/admin/timetable', '/admin/timetable?tab=schoolday', '/admin/staff/duties', '/admin/staff/duties?tab=rota', '/admin/students/class-teachers', '/content/library?tab=documents', '/admin/staff/parameters?tab=activity']) {
  await t.goto(BASE + route); await t.waitFor(`!!document.querySelector('.qm-main h1')`, 15000); await t.sleep(2500);
  const m = await t.eval(measure);
  check(`${route}: header buttons 8px apart`, m.gaps.length === 0 || m.gaps.every((g) => g === 8), JSON.stringify(m.gaps));
  check(`${route}: every default control 32px tall`, m.heights.every((h) => h === 32), JSON.stringify(m.heights));
  check(`${route}: title 20px bold`, m.h1 === '20px 700', m.h1);
  // The title's size has ONE home. Anything else winning is the 2026-09-18 bug returning.
  check(`${route}: .qm-main h1 is the rule that wins`, (m.winner || '').startsWith('.qm-main h1'), String(m.winner));
  check(`${route}: the header band is under 60px`, m.furniture === null || m.furniture <= 60, String(m.furniture));
}
await t.goto(BASE + '/admin/staff/duties'); await t.sleep(3000);
const rowGap = await t.eval(`(() => { const r = [...document.querySelectorAll('.duty-card__actions')].find((x) => x.children.length > 1); if (!r) return null; const k = [...r.children]; return Math.round(k[1].getBoundingClientRect().left - k[0].getBoundingClientRect().right); })()`);
check('a card action row (Duties) is 8px apart too', rowGap === null || rowGap === 8, String(rowGap));
await t.goto(BASE + '/admin/staff/parameters?tab=activity'); await t.sleep(3000);
const chip = await t.eval(`(() => { const c = document.querySelector('.q-daterange__preset'); if (!c) return null; const cs = getComputedStyle(c); return [Math.round(c.getBoundingClientRect().height), cs.fontSize]; })()`);
check('date-range chips are the small control size (28px, 12.5px)', chip && chip[0] === 28 && chip[1] === '12.5px', JSON.stringify(chip));
await t.goto(BASE + '/admin/users'); await t.sleep(4000);
const pager = await t.eval(`(() => { const b = document.querySelector('.rz-pager .rz-pager-element'); return b ? Math.round(b.getBoundingClientRect().height) : null; })()`);
check('the Users pager is the small control size (28px)', pager === null || pager === 28, String(pager));

await t.viewport(390, 844, true);
for (const route of ['/admin/timetable', '/admin/staff/duties', '/content/library?tab=documents']) {
  await t.goto(BASE + route); await t.waitFor(`!!document.querySelector('.qm-main h1')`, 15000); await t.sleep(2500);
  const m = await t.eval(`(() => { const hs = [...document.querySelectorAll('.qm-main .q-btn, .qm-main input.q-input, .qm-main .q-select')].filter((e) => e.getBoundingClientRect().height > 0).map((e) => Math.round(e.getBoundingClientRect().height)); const h1 = getComputedStyle(document.querySelector('.qm-main h1')).fontSize; return { min: Math.min(...hs), h1, overflow: document.documentElement.scrollWidth - innerWidth }; })()`);
  check(`390px ${route}: every control at least 40px, title 18px, no sideways scroll`, m.min >= 40 && m.h1 === '18px' && m.overflow <= 0, JSON.stringify(m));
}
check('no console errors', t.consoleErrors.length === 0, t.consoleErrors.slice(0, 3).join(' | '));
const sum = `\n  Uniform scale: ${pass} passed, ${fail} failed`; console.log(sum); post(sum);
t.close();
