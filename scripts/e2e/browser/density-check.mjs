// Measures the vertical cost of a page's furniture — the space spent before any data appears.
//
// The user's direction (2026-09-18): "this project is data driven, and therefore all forms and
// pages have to be compacted so user does not have to scroll infinitely." This is the measurement
// behind that, kept so a future change can be checked rather than eyeballed — the same reason the
// 2026-09-17 size audit exists.
//
// Run: node scripts/e2e/browser/density-check.mjs   (headless Chrome on 9333, apps on 5001/5003)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
const PAGES = [
  ['/admin/timetable/settings', 'School Day'],
  ['/admin/staff/structure', 'Departments & Structure'],
  ['/admin/students/roster', 'Student Roster'],
  ['/admin/users', 'Users & Roles'],
  ['/admin/staff', 'Staff Directory'],
];

const t = await openTab();
await t.viewport(1440, 900);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');


// An A/B in one page load: inject the PREVIOUS values, measure, remove them, measure again. Better
// evidence than two runs of different builds, and it cannot be confused by a caching difference.
const OLD_RULES = `
  .page-header { margin-bottom: 24px !important; padding-bottom: 20px !important; }
  .page-header h1 { margin: 0 0 8px !important; }
  .qm-main h1 { font-size: 28px !important; line-height: 1.25 !important; }
  .page-header .subtitle { font-size: 14px !important; line-height: normal !important; }
  .q-card__header { padding: 1.25rem !important; gap: 0.75rem !important; }
  .q-card__footer { padding: 1.25rem !important; }
  .q-card__icon { font-size: 1.25rem !important; }
  .q-card__title { font-size: 1rem !important; }
  /* .form-row had NO base rule at all, so its children stacked full-width. */
  .form-row { display: block !important; }
`;
const withOld = async (on) => t.eval(`(() => {
  const id = 'density-ab';
  document.getElementById(id)?.remove();
  if (${on}) { const s = document.createElement('style'); s.id = id; s.textContent = ${JSON.stringify(OLD_RULES)}; document.head.appendChild(s); }
  return true;
})()`);

const measure = () => t.eval(`(() => {
  const px = (el, prop) => el ? Math.round(parseFloat(getComputedStyle(el)[prop]) || 0) : 0;
  const h = (el) => el ? Math.round(el.getBoundingClientRect().height) : 0;

  const header = document.querySelector('.page-header');
  const h1 = document.querySelector('.qm-main h1');
  const cardHeaders = [...document.querySelectorAll('.q-card__header')];
  const main = document.querySelector('.qm-main');

  // "Furniture" = the page header band plus every card header: space spent before any data.
  const furniture = h(header) + px(header, 'marginBottom')
                  + cardHeaders.reduce((a, c) => a + h(c), 0);

  return JSON.stringify({
    headerBand: h(header) + px(header, 'marginBottom'),
    h1Font: px(h1, 'fontSize'),
    cardHeaders: cardHeaders.length,
    cardHeaderEach: cardHeaders.length ? Math.round(cardHeaders.reduce((a, c) => a + h(c), 0) / cardHeaders.length) : 0,
    furniture,
    docHeight: Math.round(document.documentElement.scrollHeight),
    viewport: window.innerHeight
  });
})()`);

console.log('  page                        before  after   saved    document height');
let totalBefore = 0, totalAfter = 0;
for (const [path, label] of PAGES) {
  await t.goto(BASE + path);
  const ok = await t.waitFor(`!!document.querySelector('.qm-main')`, 15000).then(() => true).catch(() => false);
  if (!ok) { console.log(`  ${label.padEnd(26)} (did not render)`); continue; }
  await t.sleep(1800);
  await withOld(true);  await t.sleep(250);
  const before = JSON.parse(await measure());
  await withOld(false); await t.sleep(250);
  const after = JSON.parse(await measure());
  totalBefore += before.furniture; totalAfter += after.furniture;
  const saved = before.furniture - after.furniture;
  console.log(
    `  ${label.padEnd(26)} ${String(before.furniture).padStart(6)}px ${String(after.furniture).padStart(6)}px ` +
    `${String(saved).padStart(7)}px  ${String(before.docHeight).padStart(6)} -> ${String(after.docHeight).padStart(6)}px`);
}
console.log(`
  furniture across ${PAGES.length} pages: ${totalBefore}px -> ${totalAfter}px  (${totalBefore - totalAfter}px saved, ${Math.round((1 - totalAfter / totalBefore) * 100)}%)`);
await t.close();
