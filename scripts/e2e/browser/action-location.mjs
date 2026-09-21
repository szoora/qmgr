// A page's action buttons sit in ONE place: top right, on the title row (2026-09-19).
//
// Reported with two screenshots: "inconsistent location of the buttons. some are on top right…
// standardise the location of the action buttons." On /admin/feedback and /admin/branches the
// section's buttons sat in a second band under the tab strip, on the left. Hub sections now render
// them into the hub's own row through QPageActions (a SectionOutlet). This suite opens every tab
// of every hub and asserts, for each:
//
//   * there is exactly ONE .page-header on the page — no second band under the tabs;
//   * every button in it sits on the title row, in the right-hand half of the band;
//   * the band is still one row tall;
//
// It measures positions, not markup, because the bug was a position.
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};

const HUBS = [
  '/admin/branches', '/admin/users', '/admin/appearance', '/admin/settings', '/admin/feedback',
  '/content/library', '/content/signage', '/billing',
  '/admin/staff', '/admin/staff/records', '/admin/staff/duties', '/admin/timetable', '/admin/staff/parameters',
];

const t = await openTab();
await t.viewport(1500, 950);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');

const MEASURE = `(() => {
  const main = document.querySelector('.qm-main');
  const bands = [...main.querySelectorAll('.page-header')].filter(b => b.offsetParent !== null);
  const band = bands[0];
  if (!band) return JSON.stringify({ bands: 0 });
  const br = band.getBoundingClientRect();
  const title = band.querySelector('h1')?.getBoundingClientRect();
  const btns = [...band.querySelectorAll('.header-actions button, .header-actions .q-btn')]
    .filter(b => b.offsetParent !== null)
    .map(b => { const r = b.getBoundingClientRect(); return { text: b.innerText.trim().slice(0, 30), left: r.left, top: r.top, bottom: r.bottom }; });
  const tabs = main.querySelector('.q-tabs');
  const tabTop = tabs ? tabs.getBoundingClientRect().top : Infinity;
  // Any action-looking row sitting BETWEEN the tabs and the first card is the old second band.
  const strays = [...main.querySelectorAll('.header-actions')]
    .filter(h => h.offsetParent !== null && !band.contains(h) && h.querySelector('button')).length;
  return JSON.stringify({
    bands: bands.length, bandH: Math.round(br.height), mid: br.left + br.width / 2,
    titleTop: title?.top, titleBottom: title?.bottom, btns, tabTop, strays,
  });
})()`;

for (const hub of HUBS) {
  await t.goto(BASE + hub);
  const ok = await t.waitFor(`!!document.querySelector('.qm-main .page-header')`, 20000).then(() => true).catch(() => false);
  if (!ok) { check(`${hub} renders`, false, 'no page header'); continue; }
  await t.sleep(1800);
  if (!(await t.eval(`location.pathname`)).startsWith(hub)) {
    console.log(`    SKIP  ${hub}  (redirected — module or permission)`); skip++; continue;
  }

  const keys = JSON.parse(await t.eval(`JSON.stringify([...document.querySelectorAll('.qm-main .q-tabs [role=tab], .qm-main .q-tabs__tab')].map(e => e.id || e.innerText.trim()))`));
  const tabNames = keys.length ? keys : ['(no tabs)'];

  for (let i = 0; i < tabNames.length; i++) {
    if (keys.length) {
      await t.eval(`(() => { const e = [...document.querySelectorAll('.qm-main .q-tabs [role=tab], .qm-main .q-tabs__tab')][${i}]; e && e.click(); return true; })()`);
      await t.sleep(1600);
    }
    const label = `${hub} › ${tabNames[i].replace(/^[a-z]+-tab-/, '')}`;
    const m = JSON.parse(await t.eval(MEASURE));

    check(`${label}: one header band`, m.bands === 1, `${m.bands} bands`);
    check(`${label}: no button band outside the header`, m.strays === 0, `${m.strays} stray action rows`);
    if (m.btns?.length) {
      const offRight = m.btns.filter(b => b.left < m.mid);
      check(`${label}: buttons on the right (${m.btns.map(b => b.text || 'icon').join(', ')})`, offRight.length === 0,
        `left of centre: ${offRight.map(b => b.text).join(', ')}`);
      const below = m.btns.filter(b => b.top >= m.tabTop);
      check(`${label}: buttons above the tabs`, below.length === 0, `below tabs: ${below.map(b => b.text).join(', ')}`);
      check(`${label}: band one row (${m.bandH}px)`, m.bandH <= 56, `${m.bandH}px`);
    }
  }
}

const tally = `\n  action-location: ${pass} passed, ${fail} failed, ${skip} skipped`;
console.log(tally); post(tally);
await t.close();
process.exit(fail ? 1 : 0);
