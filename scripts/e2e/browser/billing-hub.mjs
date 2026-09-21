// The Billing hub (2026-09-19): five routes became one, and the pages moved onto the shared layout.
//
// "these billing pages are totally off" → "implement the billing rework, use your
// recommendations". This asserts what the rework promised, in a HEADED Chrome on 9333:
//
//   1. ONE sidebar entry, no billing submenu;
//   2. the five old routes are gone, not a second copy;
//   3. the hub's five tabs for an administrator, and every ?tab= deep link opens its own tab;
//   4. each tab is on the shared layout: one header band, buttons on the title row, no hero, no
//      "Billing/…" breadcrumb, no old card grid;
//   5. the three bugs stay fixed: no raw metric key on Usage ("api_calls"), storage in MB/GB never
//      "B"/"KB", and the Payment tab says which ways to pay;
//   6. a signed-in person WITHOUT billing.view is sent to the Modules tab by a module gate and sees
//      only that tab, with no Add / Remove buttons (the API would refuse them).
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
let pass = 0, fail = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const text = (t) => t.eval(`document.querySelector('.qm-main')?.innerText ?? ''`);

const t = await openTab();
await t.viewport(1500, 950);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');

post('\n== 1. One sidebar entry ==');
await t.goto(BASE + '/billing');
await t.waitFor(`!!document.querySelector('.qm-main .q-tabs')`, 20000);
await t.sleep(1500);
const nav = JSON.parse(await t.eval(`JSON.stringify((() => {
  const link = [...document.querySelectorAll('.qm-sidebar a')].find(a => /^\\s*Billing/.test(a.innerText));
  const li = link?.closest('li');
  return { href: link?.getAttribute('href'), submenu: !!li?.querySelector('ul.submenu'),
           old: [...document.querySelectorAll('.qm-sidebar a')].map(a => a.getAttribute('href') || '').filter(h => /billing\\//.test(h)) };
})())`));
check('the sidebar has a Billing entry pointing at the hub', nav.href === 'billing', JSON.stringify(nav));
check('it has no submenu', !nav.submenu, 'submenu present');
check('no sidebar link points at a retired billing route', nav.old.length === 0, nav.old.join(', '));
check('the Billing entry is highlighted on the hub', await t.eval(`!![...document.querySelectorAll('.qm-sidebar a.active')].find(a => /Billing/.test(a.innerText))`));

post('\n== 2. The five old routes are gone ==');
// Named `retired` so suite-route-check.mjs knows these are asserted gone, not driven.
const retired = ['/billing/overview', '/billing/modules', '/billing/invoices', '/billing/payment-methods', '/billing/usage'];
for (const dead of retired) {
  await t.goto(BASE + dead);
  await t.sleep(1300);
  const body = (await t.eval(`document.body.innerText.slice(0, 200)`)).toLowerCase();
  check(`${dead} is gone, not a second copy`, /can.t be found|not found|no webpage was found|sorry/.test(body), body.slice(0, 80).replace(/\n/g, ' '));
}

post('\n== 3. Five tabs, and every deep link opens its own ==');
const TABS = { overview: 'Overview', modules: 'Modules', invoices: 'Invoices', payment: 'Payment', usage: 'Usage' };
await t.goto(BASE + '/billing');
await t.waitFor(`!!document.querySelector('.qm-main .q-tabs')`, 20000);
await t.sleep(1200);
const tabNames = JSON.parse(await t.eval(`JSON.stringify([...document.querySelectorAll('.qm-main .q-tabs__tab')].map(e => e.innerText.trim()))`));
check('an administrator sees five tabs', tabNames.length === 5, tabNames.join(', '));
check('/billing opens on Overview', /Overview/.test(await t.eval(`document.querySelector('.q-tabs__tab.active')?.innerText ?? ''`)));

for (const [key, label] of Object.entries(TABS)) {
  await t.goto(`${BASE}/billing?tab=${key}`);
  await t.waitFor(`!!document.querySelector('.qm-main .q-tabs')`, 20000);
  await t.sleep(2200);
  const active = await t.eval(`document.querySelector('.q-tabs__tab.active')?.innerText ?? ''`);
  check(`?tab=${key} opens ${label}`, active.includes(label), `active: ${active}`);

  // 4. The shared layout.
  const shape = JSON.parse(await t.eval(`JSON.stringify((() => {
    const main = document.querySelector('.qm-main');
    const bands = [...main.querySelectorAll('.page-header')].filter(b => b.offsetParent !== null);
    const band = bands[0];
    const mid = band ? band.getBoundingClientRect().left + band.getBoundingClientRect().width / 2 : 0;
    const tabsTop = main.querySelector('.q-tabs').getBoundingClientRect().top;
    const btns = band ? [...band.querySelectorAll('.header-actions button')].filter(b => b.offsetParent !== null) : [];
    const loose = [...main.querySelectorAll('button.q-btn')].filter(b => b.offsetParent !== null && !b.closest('.page-header, .q-card, .q-empty, .q-filter-bar, .q-modal, table, .q-tabs, .q-daterange, .q-select, .q-datepicker, .q-info'));
    // A dark block wider than half the page is the old wine hero.
    const hero = [...main.querySelectorAll('div')].some(d => {
      if (d.offsetParent === null) return false;
      const r = d.getBoundingClientRect(); if (r.width < main.clientWidth * 0.5 || r.height < 80) return false;
      const bg = getComputedStyle(d).backgroundColor.match(/\\d+/g)?.map(Number) ?? [255,255,255];
      return (bg[0] + bg[1] + bg[2]) / 3 < 120 && getComputedStyle(d).backgroundColor !== 'rgba(0, 0, 0, 0)';
    });
    return { bands: bands.length, bandH: band ? Math.round(band.getBoundingClientRect().height) : 0,
             leftBtns: btns.filter(b => b.getBoundingClientRect().left < mid).map(b => b.innerText.trim()),
             belowTabs: btns.filter(b => b.getBoundingClientRect().top >= tabsTop).length,
             loose: loose.map(b => b.innerText.trim() || b.title), hero,
             crumb: /Billing\\s*\\/\\s*[A-Z]/.test(main.innerText),
             oldGrid: !!main.querySelector('.modules-grid, .limit-card, .add-payment-card, .security-info, .filters-card, .usage-overview') };
  })())`));
  check(`${label}: one header band, one row (${shape.bandH}px)`, shape.bands === 1 && shape.bandH <= 56, JSON.stringify(shape));
  check(`${label}: its buttons are on the title row, top right`, shape.leftBtns.length === 0 && shape.belowTabs === 0, JSON.stringify(shape));
  check(`${label}: no button floating outside a card, table, filter bar or header`, shape.loose.length === 0, shape.loose.join(', '));
  check(`${label}: no dark hero block`, !shape.hero);
  check(`${label}: no "Billing / …" breadcrumb`, !shape.crumb);
  check(`${label}: none of the old page's layout classes`, !shape.oldGrid);
}

post('\n== 5. The three bugs stay fixed ==');
await t.goto(`${BASE}/billing?tab=usage`);
await t.waitFor(`!!document.querySelector('.qm-main .q-bars')`, 20000);
await t.sleep(1500);
const usage = await text(t);
check('Usage shows no raw metric key', !/\bapi_calls\b|\bapiCalls\b/.test(usage), usage.slice(0, 200));
check('Usage labels integration traffic as such', /Integration API calls/.test(usage));
const storageLine = usage.split('\n').find(l => /^Storage/.test(l.trim())) ?? '';
const storageFig = usage.split('\n').slice(usage.split('\n').indexOf(storageLine), usage.split('\n').indexOf(storageLine) + 3).join(' ');
check('Storage is in MB or GB, never bytes or KB', /\b\d[\d,.]*\s(MB|GB)\b/.test(storageFig) && !/\b\d[\d,.]*\s(B|KB)\b/.test(storageFig), storageFig);
check('the Usage period is written as a month, not "9/2026"', !/\b\d{1,2}\/\d{4}\b/.test(usage));

await t.goto(`${BASE}/billing?tab=payment`);
await t.waitFor(`!!document.querySelector('.qm-main table')`, 20000);
await t.sleep(1200);
const pay = await text(t);
check('Payment lists Mobile Money and Card as ways to pay', /Mobile Money/.test(pay) && /\bCard\b/.test(pay), pay.slice(0, 200));

await t.goto(`${BASE}/billing?tab=overview`);
await t.waitFor(`!!document.querySelector('.qm-main .q-stat')`, 20000);
await t.sleep(1500);
const tiles = await t.eval(`document.querySelectorAll('.qm-main .q-stat').length`);
// Five since 2026-09-19: plan, per month, modules, next renewal — and the renewal number, so it can be found.
check('Overview leads with five figures, the renewal number among them', tiles === 5 && /Renewal number/i.test(await text(t)), `${tiles} tiles`);
check('Overview shows a monthly cost in UGX', /UGX\s[\d,]+/.test(await text(t)));
t.close();

post('\n== 6. Someone without billing.view ==');
const c = await openTab();
await c.viewport(1500, 950);
await login(c, 'e2e.teacher.s2@qmgr.local', 'E2eTeacher!2026');
await c.goto(`${BASE}/billing?tab=overview`);
await c.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await c.sleep(2500);
const cPath = await c.eval('location.pathname + location.search');
const cTabs = JSON.parse(await c.eval(`JSON.stringify([...document.querySelectorAll('.qm-main .q-tabs__tab')].map(e => e.innerText.trim()))`));
check('they reach the hub rather than /unauthorized', cPath.startsWith('/billing'), cPath);
check('they see only the Modules tab', cTabs.length === 1 && /Modules/.test(cTabs[0]), cTabs.join(', '));
const cBtns = JSON.parse(await c.eval(`JSON.stringify([...document.querySelectorAll('.qm-main table button')].map(b => b.innerText.trim()).filter(Boolean))`));
check('they are offered no Add, Pay or Remove', !cBtns.some(b => /^(Add|Pay|Remove)$/.test(b)), cBtns.join(', '));
const cNav = await c.eval(`!![...document.querySelectorAll('.qm-sidebar a')].find(a => /^\\s*Billing/.test(a.innerText))`);
check('and no Billing entry in their sidebar', !cNav);
c.close();

const tally = `\n  billing-hub: ${pass} passed, ${fail} failed`;
console.log(tally); post(tally);
process.exit(fail ? 1 : 0);
