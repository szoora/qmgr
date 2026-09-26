// THE PHONE NAVIGATION BAR — the right slots for the role, and nothing covered.
//
// Under 768px every one of this app's 47 destinations sat behind the hamburger, and hiding the main
// navigation cuts discoverability almost in half (NN/g). The bar is four role-chosen slots plus
// Notifications and More.
//
// What this asserts:
//   1. it renders under 768px and NOT above it;
//   2. exactly six slots, ending Alerts + More, and every label fits (no truncation);
//   3. each slot is at least 44px (WCAG 2.5.5) and none overlaps another;
//   4. the band is RESERVED — .qm-main's bottom padding covers the bar, so nothing is underneath;
//   5. pressing a slot navigates and lights it, and a hub's ?tab= keeps it lit;
//   6. More opens a sheet, the sheet reaches the rest, and it shuts on navigation;
//   7. THE SLOTS DIFFER BY ROLE, and no slot 403s for the person who was offered it.
//
// (7) is the point of the whole feature and the one an API suite cannot check.
//
// Run: node scripts/e2e/browser/mobile-nav.mjs   (CDP_PORT=9444 to watch it)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';

const ADMIN = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const ADMIN_PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';
const TEACHER = process.env.E2E_TEACHER_USER ?? 'e2e.sp.math1@qmgr.local';
const TEACHER_PASSWORDS = process.env.E2E_TEACHER_PASS
  ? [process.env.E2E_TEACHER_PASS]
  : ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];

let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const say = (l) => { console.log(l); post(l); };
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  say(`    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`);
};
const note = (name, why) => { skip++; say(`    SKIP  ${name}  — ${why}`); };

const findPassword = async (email, passwords) => {
  for (const password of [].concat(passwords)) {
    const r = await fetch(`${API}/api/v1/auth/login`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }),
    });
    if (r.ok) return password;
  }
  return null;
};

const teacherPass = await findPassword(TEACHER, TEACHER_PASSWORDS);
if (!await findPassword(ADMIN, ADMIN_PASS)) { console.error(`could not sign in as ${ADMIN}`); process.exit(1); }

const t = await openTab();

// Reads the bar as it stands. No regex: escaping one through two layers has cost this project time.
const BAR = `(() => {
  const bar = document.querySelector('.qm-mobilenav');
  if (!bar) return JSON.stringify({ present: false });
  const vh = window.innerHeight, vw = document.documentElement.clientWidth;
  const r = bar.getBoundingClientRect();
  const slots = [...bar.querySelectorAll('.qm-mobilenav__slot')].map(s => {
    const sr = s.getBoundingClientRect();
    const label = s.querySelector('.qm-mobilenav__label');
    return {
      label: label ? label.innerText.trim() : '',
      w: Math.round(sr.width), h: Math.round(sr.height),
      left: Math.round(sr.left), right: Math.round(sr.right),
      active: s.classList.contains('qm-mobilenav__slot--active'),
      href: s.getAttribute('href'),
      // A label wider than its box is truncated — the design error the bar's own CSS hides.
      clipped: label ? label.scrollWidth > label.clientWidth + 1 : false,
      badge: !!s.querySelector('.qm-mobilenav__badge'),
    };
  });
  let overlap = 0;
  for (let i = 1; i < slots.length; i++) if (slots[i].left < slots[i - 1].right - 1) overlap++;
  const main = document.querySelector('.qm-main');
  return JSON.stringify({
    present: true,
    display: getComputedStyle(bar).display,
    top: Math.round(r.top), height: Math.round(r.height), vh, vw,
    atBottom: Math.abs(r.bottom - vh) <= 1,
    slots, overlap,
    mainPadBottom: parseFloat(getComputedStyle(main).paddingBottom) || 0,
    sheet: !!document.querySelector('.qm-moresheet'),
  });
})()`;

const read = async () => JSON.parse(await t.eval(BAR));

const pressSlot = (label) => t.eval(`(() => {
  const s = [...document.querySelectorAll('.qm-mobilenav__slot')]
    .find(x => (x.querySelector('.qm-mobilenav__label') || {}).innerText === ${JSON.stringify(label)});
  if (!s) return false; s.click(); return true; })()`);

// ---- 1. the breakpoint --------------------------------------------------------------------------
say('  1. The breakpoint');
await t.viewport(390, 844, true);
await login(t, ADMIN, ADMIN_PASS);
await t.goto(`${BASE}/portal`);
await t.waitFor("!!document.querySelector('.qm-main')", 20000);
await t.sleep(2200);

let r = await read();
check('the bar renders at 390px', r.present && r.display === 'flex', `present=${r.present} display=${r.display}`);
if (!r.present) { t.close(); say(`  mobile-nav: ${pass} passed, ${fail} failed`); process.exitCode = 1; }

check('...pinned to the bottom of the viewport', r.atBottom, `bottom vs vh: top=${r.top} h=${r.height} vh=${r.vh}`);

await t.viewport(1280, 900);
await t.sleep(900);
const wide = await read();
check('the bar is hidden at 1280px', wide.display === 'none', `display=${wide.display}`);
await t.viewport(390, 844, true);
await t.sleep(900);

// ---- 2 & 3. shape and targets --------------------------------------------------------------------
say('  2. Shape, labels and targets');
r = await read();
check('six slots: four chosen, Alerts, More', r.slots.length === 6, `${r.slots.length}: ${r.slots.map(s => s.label).join(' | ')}`);
check('...ending Alerts then More', r.slots.at(-2)?.label === 'Alerts' && r.slots.at(-1)?.label === 'More',
  r.slots.map(s => s.label).join(' | '));
check('no label is truncated', r.slots.every(s => !s.clipped),
  r.slots.filter(s => s.clipped).map(s => s.label).join(', '));
check('every slot is at least 44px tall (WCAG 2.5.5)', r.slots.every(s => s.h >= 44),
  r.slots.map(s => `${s.label}:${s.h}`).join(' '));
check('no two slots overlap', r.overlap === 0, `${r.overlap} pair(s)`);

// ---- 4. the band is reserved ---------------------------------------------------------------------
say('  4. The band is reserved, not overlaid');
check('.qm-main reserves at least the bar height', r.mainPadBottom >= r.height,
  `padding ${r.mainPadBottom}px vs bar ${r.height}px`);

// Nothing interactive may be UNDER the bar. Hit-tested, because a rect crossing the bar means
// nothing if the element is clipped or the bar is on top — the correction mobile-readiness needed.
const covered = await t.eval(`(() => {
  const bar = document.querySelector('.qm-mobilenav');
  const br = bar.getBoundingClientRect();
  const els = [...document.querySelectorAll('a[href], button, input, select, textarea')];
  const bad = els.filter(el => {
    if (bar.contains(el)) return false;
    const r = el.getBoundingClientRect();
    if (r.bottom <= br.top || r.top >= br.bottom) return false;
    const x = Math.min(Math.max(r.left + r.width / 2, 1), window.innerWidth - 1);
    const y = Math.min(Math.max(r.top + r.height / 2, 1), window.innerHeight - 1);
    const hit = document.elementFromPoint(x, y);
    return !!hit && (hit === el || el.contains(hit));
  });
  return JSON.stringify(bad.slice(0, 4).map(el => el.tagName.toLowerCase() + '.' + [...el.classList].slice(0,2).join('.')));
})()`);
check('nothing usable sits under the bar', JSON.parse(covered).length === 0, covered);

// ---- 5. pressing a slot --------------------------------------------------------------------------
say('  5. Pressing a slot');
const target = r.slots.find(s => s.label !== 'More' && s.label !== 'Alerts');
if (target) {
  await pressSlot(target.label);
  await t.sleep(1800);
  const after = await read();
  const lit = after.slots.find(s => s.label === target.label);
  check(`pressing "${target.label}" navigates`, (await t.eval('location.pathname')) === target.href,
    `${await t.eval('location.pathname')} vs ${target.href}`);
  check('...and the slot lights up', lit?.active === true, JSON.stringify(lit));
} else {
  note('pressing a slot', 'no navigable slot on the bar');
}

// A hub's ?tab= must keep its slot lit — the bug MainLayout.IsActive already had to fix.
await t.goto(`${BASE}/admin/staff?tab=departments`);
await t.waitFor("!!document.querySelector('.qm-main')", 20000);
await t.sleep(1800);
const hub = await read();
const staffSlot = hub.slots.find(s => s.href === '/admin/staff');
if (staffSlot) check('a hub tab keeps its slot lit', staffSlot.active === true, JSON.stringify(staffSlot));
else note('hub tab lighting', 'this role has no Staff slot');

// ---- 6. the More sheet ---------------------------------------------------------------------------
say('  6. The More sheet');
await pressSlot('More');
await t.sleep(900);
const sheet = await t.eval(`(() => {
  const s = document.querySelector('.qm-moresheet');
  if (!s) return JSON.stringify({ open: false });
  const items = [...s.querySelectorAll('.qm-moresheet__item')];
  const groups = [...s.querySelectorAll('.qm-moresheet__group')].map(g => g.innerText.trim());
  const barLabels = [...document.querySelectorAll('.qm-mobilenav__label')].map(l => l.innerText.trim());
  const dupes = items.map(i => i.innerText.trim()).filter(x => barLabels.includes(x));
  return JSON.stringify({
    open: true, items: items.length, groups,
    small: items.filter(i => i.getBoundingClientRect().height < 44).length,
    dupes,
    aboveBar: (() => { const r = s.getBoundingClientRect(); return Math.abs(r.bottom - window.innerHeight) <= 1; })(),
  });
})()`);
const sh = JSON.parse(sheet);
check('More opens a sheet', sh.open === true, sheet);
check('...with destinations in it', sh.items > 0, `${sh.items}`);
check('...grouped', (sh.groups || []).length > 0, JSON.stringify(sh.groups));
check('...no destination repeated from the bar', (sh.dupes || []).length === 0, JSON.stringify(sh.dupes));
check('...every row at least 44px', sh.small === 0, `${sh.small} under`);

// It must shut on ANY navigation, not only on a press inside it.
await t.goto(`${BASE}/notifications`);
await t.waitFor("!!document.querySelector('.qm-main')", 20000);
await t.sleep(1500);
check('the sheet shuts on navigation', !(await read()).sheet, 'still open after a route change');

// ---- 7. the slots differ by role, and none refuses -------------------------------------------------
say('  7. The slots differ by role');
const adminBar = (await read()).slots.map(s => s.label);

if (!teacherPass) {
  note('a teacher gets a different bar', `could not sign in as ${TEACHER}`);
} else {
  await login(t, TEACHER, teacherPass);
  await t.goto(`${BASE}/portal`);
  await t.waitFor("!!document.querySelector('.qm-main')", 20000);
  await t.sleep(2200);
  const teacher = await read();
  const teacherBar = teacher.slots.map(s => s.label);
  check('a teacher gets a bar of their own', teacherBar.length === 6, teacherBar.join(' | '));
  check('...different from the administrator\'s', teacherBar.join('|') !== adminBar.join('|'),
    `both are ${teacherBar.join(' | ')}`);
  // Reported 2026-09-26: the bar was chosen before the module list arrived, so a teacher saw Home · Alerts · More while
  // the sidebar offered My Workspace and My School Day. Whatever the sidebar shows first, the bar must carry.
  check('...carrying My Day and Workspace, as the sidebar does', teacherBar.includes('My Day') && teacherBar.includes('Workspace'),
    teacherBar.join(' | '));
  // 2026-09-26: Home repeats My Workspace for anybody who holds it; the calendar is what a phone is opened to check.
  check('...with Calendar and not Home', teacherBar.includes('Calendar') && !teacherBar.includes('Home'), teacherBar.join(' | '));

  // THE ASSERTION THAT MATTERS: a slot offered must not refuse. A bar that shows a destination the
  // server then 403s is worse than not showing it.
  const refused = [];
  for (const slot of teacher.slots) {
    if (!slot.href || slot.href === '#more') continue;
    await t.goto(`${BASE}${slot.href}`);
    await t.waitFor("!!document.querySelector('.qm-main')", 15000);
    await t.sleep(1300);
    const landed = await t.eval('location.pathname');
    // A module gate sends the person to the Billing HUB (BillingLinks.Hub = /billing), not to a
    // /billing/modules route — that one was retired when billing became one hub, and naming it
    // here would have been a check that could never fire. Landing on /billing is only a refusal
    // when Billing is not where the slot pointed.
    const gated = landed.startsWith('/billing') && !slot.href.startsWith('/billing');
    if (landed.startsWith('/unauthorized') || gated) {
      refused.push(`${slot.label} → ${landed}`);
    }
  }
  check('no slot a teacher is offered refuses them', refused.length === 0, refused.join(', '));
}

t.close();
say('');
const tail = `  mobile-nav: ${pass} passed, ${fail} failed, ${skip} skipped`;
say(tail);
process.exitCode = fail ? 1 : 0;
