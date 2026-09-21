// THE LIST-PAGE SWEEP, in a browser (plan docs/plans/LIST_PAGE_STANDARDISATION.md).
//
// list-page-audit.mjs reads the source and says a page HAS a bar and a pager. Only opening it says
// they RENDER, that the page still draws its rows, and that nothing throws on the way — which is
// the difference the register rework itself was built on ("a clean build is not verification").
//
// Run: node scripts/e2e/browser/list-sweep.mjs   (headless Chrome on 9333)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';

let pass = 0, fail = 0;
const post = (l) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: l + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};

// Each page the sweep touched, and what should now be true of it. `bar` = a QBulkBar is rendered;
// `pager` = a QPager is rendered ONCE THERE IS ENOUGH TO PAGE, so its absence is only a failure
// when the page says it has more rows than a page holds — which the suite cannot assume on a dev
// tenant. So the pager is reported, never failed, and the hard assertions are: the page renders,
// it does not scroll sideways, and nothing threw.
const PAGES = [
  { url: '/admin/visitors/audit', name: 'Visitor deletion audit', bar: true },
  { url: '/admin/users?tab=requests', name: 'Join requests', bar: true },
  { url: '/admin/welfare-my-actions', name: 'My welfare actions', bar: true },
  { url: '/admin/visitors/expected', name: 'Expected visitors', bar: true },
  { url: '/admin/staff/parameters?tab=notices', name: 'Staff notices', bar: true },
  { url: '/admin/staff/duties?tab=reports', name: 'Duty reports queue', bar: true },
  { url: '/admin/visitors', name: 'Visitor management', bar: false },
  { url: '/admin/welfare-reports', name: 'Welfare reports', bar: false },
  { url: '/content/library?tab=media', name: 'Media library', bar: false },
  { url: '/content/signage?tab=playlists', name: 'Playlists', bar: false },
  { url: '/content/signage?tab=campaigns', name: 'Campaigns', bar: false },
  { url: '/content/signage?tab=schedules', name: 'Schedules', bar: false },
  { url: '/content/signage?tab=zones', name: 'Display zones', bar: false },
  { url: '/admin/marketing', name: 'Broadcasts', bar: false },
  { url: '/admin/timetable?tab=subjects', name: 'Subjects', bar: false },
  { url: '/admin/timetable?tab=schoolday', name: 'School day (Phase 3 guard)', bar: false },
];

const t = await openTab();
await t.viewport(1600, 950);
await login(t, USER, PASS);
await t.eval(`localStorage.setItem('qmgr-branch', ${JSON.stringify(JSON.stringify(BRANCH))}); true`);

console.log('\n== The swept pages, opened ==');
post('\n== The swept pages, opened ==');

for (const p of PAGES) {
  await t.goto(`${BASE}${p.url}`);
  const ready = await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
  await t.sleep(1600);

  const state = await t.eval(`(() => {
    const err = document.querySelector('#blazor-error-ui');
    return {
      url: location.pathname + location.search,
      // A page whose module the tenant does not hold REDIRECTS to Billing. Passing 'renders' on
      // the billing page would be a vacuous pass — exactly the trap CLAUDE.md warns about — so it
      // is detected and reported as a skip with the reason.
      redirected: location.pathname.startsWith('/billing'),
      bar: document.querySelectorAll('.q-bulkbar').length,
      pager: document.querySelectorAll('.q-pager, [class*="pager"]').length,
      docW: document.documentElement.scrollWidth,
      winW: window.innerWidth,
      errShown: err ? getComputedStyle(err).display !== 'none' : false,
      // The bar lives inside the page's "there are rows" branch, which is right — a bar over an
      // empty list is furniture. So an empty state showing means the bar is correctly absent.
      empty: !!document.querySelector('.q-empty, .empty-state'),
      text: (document.querySelector('.qm-main')?.innerText || '').slice(0, 60).replace(/\\s+/g, ' '),
    };
  })()`);

  const label = `${p.name}`;
  if (state.redirected) {
    const l = `    SKIP  ${label}: this tenant does not hold the module, so the page redirects to Billing`;
    console.log(l); post(l); continue;
  }
  check(`${label}: renders`, ready === true && state.text.length > 0, `text: "${state.text}"`);
  check(`${label}: no error bar`, state.errShown === false, 'a Blazor error bar is showing');
  check(`${label}: no sideways scroll`, state.docW <= state.winW + 1, `${state.docW} > ${state.winW}`);
  // The bar renders inside the page's 'there are rows' branch, which is right: a bar over an
  // empty list is furniture. So it is only ASSERTED when the page drew rows, and reported otherwise.
  if (p.bar && !state.empty) check(`${label}: the shared bar is rendered`, state.bar > 0, 'rows are drawn but no .q-bulkbar');
  else if (p.bar) { const l = `    SKIP  ${label}: the page is showing its empty state, so the bar is correctly absent`; console.log(l); post(l); }
  const note = `           ${p.name}: ${state.bar} bar(s), ${state.pager} pager(s)`;
  console.log(note); post(note);
}

// The Phase 3 guard on the School Day page: a nameless room must be refused BEFORE either call.
console.log('\n== Phase 3: the client refuses what the server would ==');
post('\n== Phase 3: the client refuses what the server would ==');
await t.goto(`${BASE}/admin/timetable?tab=schoolday`);
await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
await t.sleep(2200);
const added = await t.eval(`(() => {
  const b = [...document.querySelectorAll('button')].find(e => /Add a room/i.test(e.innerText));
  if (!b) return 'no add-room button';
  b.click(); return 'added';
})()`);
if (added !== 'added') {
  const line = `    SKIP  Phase 3: ${added} — the room editor may need staff.duties.manage`;
  console.log(line); post(line);
} else {
  await t.sleep(900);
  await t.eval(`[...document.querySelectorAll('button')].find(e => e.innerText.trim() === 'Save')?.click()`);
  await t.sleep(1500);
  const refusal = await t.eval(`(document.querySelector('.qm-main')?.innerText || '') + ' ' + (document.body.innerText || '')`);
  check('Phase 3: a blank room is refused by the PAGE, naming the row',
    /has no name/i.test(refusal), 'the page submitted it instead');
}

console.log(`\n${pass} passed, ${fail} failed`);
post(`\n${pass} passed, ${fail} failed`);
t.close();
process.exit(fail ? 1 : 0);
