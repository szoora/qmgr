// THE SCHOOL DAY'S ROWS FOLLOW THEIR TIMES (2026-09-24).
//
// "how to edit school day. i need to move p4 to before break. ui has no way to do this." The server always sorted the
// periods by start time on save; the page did not, so a period moved earlier sat below the one it now came before and
// the reader could not see what they had done until they saved. The rows now re-sort the moment a start time changes.
// Section 39 proves the save and the re-timed lessons; this proves what the reader SEES, which no API suite can.
//
//   1  changing a period's start to before the row above moves it above that row — before any save
//   2  its key and label travel with it (a lesson is stored against the key)
//   3  nothing is saved by the reorder alone: a reload brings the stored order back
//
// Run: API and Web up (127.0.0.1:5001 / :5003), Chrome on CDP_PORT (default 9333), then
//   node scripts/e2e/browser/school-day-order.mjs
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
let pass = 0, fail = 0;
const post = (l) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: l + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => { ok ? pass++ : fail++; const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`; console.log(l); post(l); };

const t = await openTab();
try {
  await t.viewport(1440, 950);
  await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');
  await t.goto(`${BASE}/admin/timetable?tab=schoolday`);
  const came = await t.waitFor(`document.querySelectorAll('table tbody tr input[type=time]').length >= 4`, 25000);
  check('the School Day editor opens with its periods', came);

  // The first day type's rows: [key, label, start] in the order the page shows them.
  const rows = () => t.eval(`[...document.querySelector('table.tts-periods tbody').querySelectorAll('tr')].map(r => {
    const i = r.querySelectorAll('input'); return [i[0]?.value, i[1]?.value, i[2]?.value]; })`);
  const before = await rows();
  // Take the LAST row and give it the start time of the row above it minus a minute — earlier than its neighbour.
  const n = before.length - 1, above = before[n - 1];
  const [h, m] = above[2].split(':').map(Number);
  const earlier = `${String(Math.floor((h * 60 + m - 1) / 60)).padStart(2, '0')}:${String((h * 60 + m - 1) % 60).padStart(2, '0')}`;
  const moving = before[n];
  const set = await t.eval(`(() => {
    const row = document.querySelector('table.tts-periods tbody').querySelectorAll('tr')[${n}];
    const input = row.querySelectorAll('input')[2];
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
    setter.call(input, ${JSON.stringify(earlier)});
    input.dispatchEvent(new Event('input', { bubbles: true })); input.dispatchEvent(new Event('change', { bubbles: true }));
    return true; })()`);
  await t.sleep(1200);
  const after = await rows();
  const at = after.findIndex((r) => r[0] === moving[0]);
  check(`1: ${moving[1]} moved to ${earlier} jumps above ${above[1]} straight away`, set && at === n - 1, JSON.stringify(after.map((r) => r[1] + ' ' + r[2])));
  check(`2: …with its key and label (${moving[0]} / ${moving[1]})`, at >= 0 && after[at][1] === moving[1] && after[at][2] === earlier, JSON.stringify(after[at]));
  const errorBar = await t.eval(`getComputedStyle(document.querySelector('#blazor-error-ui')).display !== 'none'`);
  check('…and no error bar', !errorBar);

  await t.goto(`${BASE}/admin/timetable?tab=schoolday`);
  await t.waitFor(`document.querySelectorAll('table tbody tr input[type=time]').length >= 4`, 25000);
  await t.sleep(800);
  const reloaded = await rows();
  check('3: nothing was saved by the reorder alone — a reload shows the stored order', JSON.stringify(reloaded) === JSON.stringify(before), JSON.stringify(reloaded.map((r) => r[1] + ' ' + r[2])));
} catch (e) {
  check('the suite ran to the end', false, e.message);
} finally {
  t.close();
}
const line = `  school-day-order: ${pass} passed, ${fail} failed`;
console.log(line); post(line);
process.exitCode = fail ? 1 : 0;
