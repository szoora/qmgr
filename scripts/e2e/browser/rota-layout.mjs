// THE ROTA COMES FIRST, AND THE LOAD CARD SAYS WHAT IT MEASURES (2026-09-24).
//
// "is fairness descriptive of the feature? it should also come at the bottom. the most important data is the duty
// itself" — the per-person count sat ABOVE the rota, naming every member of staff before the duties, under the word
// "Fairness", which is a judgement rather than a label. It is "Duty load" now, below the week grid and the month list.
//
//   1  the Rota tab opens, week view, with no error bar
//   2  the card reads "Duty load", and nothing on the page says "Fairness"
//   3  it comes AFTER the rota in the document, in the week view and in the month view
//
// Run: API and Web up (127.0.0.1:5001 / :5003), Chrome on CDP_PORT (default 9333), then
//   node scripts/e2e/browser/rota-layout.mjs
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
let pass = 0, fail = 0;
const post = (l) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: l + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => { ok ? pass++ : fail++; const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`; console.log(l); post(l); };

// Where the rota and the load card sit in document order: -1 before, 1 after, 0 one of them missing.
const ORDER = `(() => {
  const card = [...document.querySelectorAll('.rota-fair')][0];
  const rota = document.querySelector('.q-weekgrid, .q-monthlist');
  if (!card || !rota) return { card: !!card, rota: !!rota, after: null };
  return { card: true, rota: true, after: !!(rota.compareDocumentPosition(card) & Node.DOCUMENT_POSITION_FOLLOWING),
           title: card.querySelector('.q-card__title, h3, h2')?.textContent?.trim() ?? card.textContent.slice(0, 60) };
})()`;

const t = await openTab();
try {
  await t.viewport(1440, 950);
  await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');
  await t.goto(`${BASE}/admin/staff/duties?tab=rota`);
  const came = await t.waitFor(`!!document.querySelector('.rota-page') && !document.querySelector('.loading-container')`, 25000);
  const errorBar = await t.eval(`getComputedStyle(document.querySelector('#blazor-error-ui')).display !== 'none'`);
  check('1: the Rota tab opens with no error bar', came && !errorBar);

  const hasCard = await t.waitFor(`!!document.querySelector('.rota-fair')`, 10000);
  if (!hasCard) {
    console.log('    SKIP  no rota slots this term, so there is no load card to place'); post('    SKIP  no load card this term');
  } else {
    const week = await t.eval(ORDER);
    check('2: the card reads "Duty load"', /^Duty load/.test(week.title ?? ''), JSON.stringify(week));
    check('…and the word "Fairness" is nowhere on the page', !(await t.eval(`document.querySelector('.rota-page').innerText.includes('Fairness')`)));
    check('3: week view — the load card comes after the rota', week.after === true, JSON.stringify(week));

    await t.eval(`[...document.querySelectorAll('.rota-page button, .rota-page [role=tab]')].find(b => b.textContent.trim() === 'Month')?.click()`);
    await t.sleep(1500);
    const month = await t.eval(ORDER);
    check('…month view — the load card comes after the rota', month.after === true, JSON.stringify(month));
  }
} catch (e) {
  check('the suite ran to the end', false, e.message);
} finally {
  t.close();
}
const line = `  rota-layout: ${pass} passed, ${fail} failed`;
console.log(line); post(line);
process.exitCode = fail ? 1 : 0;
