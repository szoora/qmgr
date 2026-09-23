// The Onboarding tab's Status list (2026-09-23): one list under the shared QBulkBar, chips carrying
// the counts, QCheckbox rows — not three stat tiles, a hand-rolled bulk strip and raw browser
// checkboxes. Read-only: it ticks and clears, never re-issues anybody's password.
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const B = 'http://127.0.0.1:5003';
let pass = 0, fail = 0;
const ok = (c, m) => { c ? pass++ : fail++; console.log(`${c ? 'PASS' : 'FAIL'}  ${m}`); };

const t = await openTab();
await t.viewport(1600, 1000);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');
await t.goto(`${B}/admin/users?tab=onboarding`);
const ready = await t.waitFor(`!!document.querySelector('.q-bulkbar') || document.body.innerText.includes('part of Welfare')`);
if (!ready || await t.eval(`document.body.innerText.includes('part of Welfare')`)) {
  console.log('SKIP  the tenant does not hold Welfare & Performance, so there is no Status tab to check');
  process.exit(0);
}
await t.sleep(800);

ok(await t.eval(`!document.querySelector('.admin-page .q-stat-row, .admin-page .q-stat-tile')`), 'no summary stat tiles');
ok(await t.eval(`!document.querySelector('.ob-bulk')`), 'no hand-rolled bulk strip');
ok(await t.eval(`document.querySelectorAll('.q-bulkbar').length === 1`), 'exactly one QBulkBar');
const chips = await t.eval(`[...document.querySelectorAll('.q-bulkbar__chip')].map(c => c.innerText.replace(/\\s+/g,' ').trim())`);
ok(chips.length >= 2 && chips.some(c => c.startsWith('Not signed in yet')), `chips carry the lists and their counts: ${chips.join(' | ')}`);
ok(await t.eval(`[...document.querySelectorAll('.admin-page input[type=checkbox]')].every(i => i.classList.contains('q-checkbox__input'))`),
   'every checkbox on the page is QCheckbox');
ok(await t.eval(`document.querySelectorAll('.admin-page table.data-table').length <= 1`), 'one table, not three');

const rows = await t.eval(`document.querySelectorAll('.admin-page table.data-table tbody tr').length`);
if (rows === 0) {
  console.log('SKIP  the chosen list is empty on this tenant, so selection cannot be exercised');
} else {
  ok(rows <= 25, `the list pages (${rows} rows rendered)`);
  // Select all shown, then read the count the bar reports.
  await t.eval(`document.querySelector('.q-bulkbar__all input').click(); true`);
  await t.sleep(500);
  const count = await t.eval(`(document.querySelector('.q-bulkbar__count')?.innerText || '')`);
  const shown = await t.eval(`(document.querySelector('.q-bulkbar__all')?.innerText.match(/\\d+/) || ['0'])[0]`);
  ok(count.startsWith(shown + ' '), `select-all ticks exactly the rows shown (${count} of ${shown})`);
  ok(await t.eval(`[...document.querySelectorAll('.q-bulkbar__bulk button')].some(b => b.innerText.includes('Print slips'))`), 're-issue actions sit in the bar');
  // A search narrows the list and must prune the selection with it.
  const first = await t.eval(`document.querySelector('.admin-page table.data-table tbody tr .ob-person-text span').innerText`);
  await t.setValue('.q-bulkbar__search input', first);
  await t.sleep(700);
  const after = await t.eval(`(document.querySelector('.q-bulkbar__count')?.innerText || '0')`);
  const shown2 = await t.eval(`(document.querySelector('.q-bulkbar__all')?.innerText.match(/\\d+/) || ['0'])[0]`);
  ok(parseInt(after) <= parseInt(shown2), `a search prunes the selection to what is visible (${after}, ${shown2} shown)`);
  await t.eval(`[...document.querySelectorAll('.q-bulkbar__bulk button')].find(b => b.innerText.includes('Clear selection'))?.click(); true`);
}
ok(await t.eval(`getComputedStyle(document.getElementById('blazor-error-ui')).display === 'none'`), 'no error bar');
await t.shot(process.env.SHOT ?? 'onboarding-status.png');
console.log(`\n${pass} passed, ${fail} failed`);
t.close();
process.exitCode = fail ? 1 : 0;
