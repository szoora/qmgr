// THE TERM PROGRAMME IMPORT, IN A BROWSER — the school's five real documents through the page.
//
// Plan: docs/plans/TERM_PROGRAMME_CALENDAR_AND_GATES.md §4–§8, §12. The API suite (section 31) proves the
// readers and the server; this one proves the PAGE: that five files chosen at once are each classified as what
// they are, that the things only a person can decide are put to them (the UCE briefing conflict, the Wed 2 Dec
// gap with its one-digit suggestion, the married-name question), and that the per-person rota preview is there.
//
// SKIPPED without E2E_DOCS_DIR: the documents carry staff phone numbers and are never in this repository.
// It stops BEFORE the commit unless E2E_COMMIT=1 — then it answers every question, imports, re-imports
// (nothing new) and undoes, leaving the tenant as it found it.
//
// Run: E2E_DOCS_DIR="D:/QMGR/DATA" node scripts/e2e/browser/programme-import.mjs   (CDP_PORT=9444 to watch it)
import fs from 'node:fs';
import path from 'node:path';
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';
const DOCS = process.env.E2E_DOCS_DIR ?? '';
const COMMIT = process.env.E2E_COMMIT === '1';

let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const say = (l) => { console.log(l); post(l); };
const check = (name, ok, detail = '') => { ok ? pass++ : fail++; say(`    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`); };
const note = (name, why) => { skip++; say(`    SKIP  ${name}  — ${why}`); };
const finish = () => { say(`  programme-import: ${pass} passed, ${fail} failed, ${skip} skipped`); process.exitCode = fail ? 1 : 0; };

if (!DOCS || !fs.existsSync(DOCS)) {
  note('the whole suite', 'E2E_DOCS_DIR is not set — the school\'s documents carry staff phone numbers and are never in this repository');
  finish();
  process.exit(0);
}
const all = fs.readdirSync(DOCS).filter((f) => /\.(docx|doc|pdf)$/i.test(f));
const expected = [
  [/^Activities/i, 'TermActivities'],
  [/^BEGINNING/i, 'DailyProgramme'],
  [/^Schedule of Meetings/i, 'MeetingSchedule'],
  [/^STAFF DUTY ROTA/i, 'PersonRota'],
  [/^ADMINISTRATIVE/i, 'PeriodRota'],
].map(([re, kind]) => ({ name: all.find((f) => re.test(f)), kind })).filter((x) => x.name);
if (expected.length < 5) {
  note('the whole suite', `only ${expected.length} of the five documents were found in ${DOCS}`);
  finish();
  process.exit(0);
}
const paths = expected.map((x) => path.resolve(DOCS, x.name));

const t = await openTab();
await t.viewport(1600, 1000);
await login(t, USER, PASS);

async function chooseFiles() {
  await t.goto(`${BASE}/calendar?tab=import`);
  if (!await t.waitFor("!!document.querySelector('#pi-files')", 25000)) return false;
  await t.sleep(800);
  await t.setFiles('#pi-files', paths);
  return t.waitFor("document.querySelectorAll('.pi-table').length >= 5", 60000);
}

say('  1. Five documents chosen at once, each read as what it is');
if (!await chooseFiles()) {
  check('the import section opens and reads the files', false, await t.eval('document.body.innerText.slice(0, 400)'));
  t.close(); finish(); process.exit(1);
}
await t.sleep(1000);
const kinds = JSON.parse(await t.eval(`JSON.stringify([...document.querySelectorAll('.pi-table')].map(e => ({ file: e.dataset.file, kind: e.dataset.kind })))`));
for (const x of expected) {
  const got = kinds.find((k) => k.file === x.name)?.kind;
  check(`${x.name} → ${x.kind}`, got === x.kind, `read as ${got}`);
}
check('the binary .doc was read, not refused', await t.eval(`!document.body.innerText.includes('older Word document this reader could not follow')`));

say('  2. What must be decided is put to the reader');
await t.clickText('Continue', '#pi-continue');
if (!await t.waitFor("!!document.querySelector('#pi-must-decide')", 30000)) {
  check('the check step opens', false, await t.eval('document.body.innerText.slice(0, 400)'));
  t.close(); finish(); process.exit(1);
}
await t.sleep(1200);
const issues = JSON.parse(await t.eval(`JSON.stringify([...document.querySelectorAll('#pi-must-decide li.pi-issue')].map(li => ({ kind: li.dataset.kind, date: li.dataset.date, person: li.dataset.person, text: li.innerText })))`));
const warnings = await t.eval(`document.querySelector('#pi-warnings') ? document.querySelector('#pi-warnings').innerText : ''`);
const conflict = issues.find((i) => i.kind === 'Conflict');
check('the documents\' disagreement about the UCE briefing is asked', !!conflict && /UCE/.test(conflict.text), JSON.stringify(issues.filter((i) => i.kind === 'Conflict')));
check('…offering both dates', !!conflict && /08 Oct 2026/.test(conflict.text) && /12 Oct 2026/.test(conflict.text), conflict?.text);
const gap = issues.find((i) => i.kind === 'RotaGap' && i.date === '2026-12-02');
check('the Wed 2 Dec gap is asked', !!gap, JSON.stringify(issues.filter((i) => i.kind === 'RotaGap')));
check('…with the one-digit fix beside it: Use 02/12', !!gap && /Use 02\/12/.test(gap.text) && /Musiime Naomeh/i.test(gap.text), gap?.text);
const married = issues.find((i) => /Asiimwe Aisha Peace/i.test(i.person ?? i.text)) ?? (/Asiimwe Aisha Peace/i.test(warnings) ? { text: warnings } : null);
check('the married-name question about "Asiimwe Aisha Peace" is raised', !!married, JSON.stringify(issues.filter((i) => i.kind === 'NameQuestion').slice(0, 5)));
check('the two doubles are warned about', /12 Sep/.test(warnings) && /2 Nov/.test(warnings), warnings.slice(0, 300));

say('  3. The previews');
const people = await t.eval(`document.querySelectorAll('#pi-per-person li').length`);
check('the per-person rota preview lists the people on the rota', people >= 50, `${people} people`);
const agenda = await t.eval(`document.querySelectorAll('#pi-agenda .pi-day').length`);
check('the term preview lists days', agenda >= 10, `${agenda} days`);
check('Check with the server waits for every answer', await t.eval(`document.querySelector('#pi-preview')?.disabled === true`));
check('no error bar', await t.eval(`getComputedStyle(document.querySelector('#blazor-error-ui') || document.body).display === 'none' || !document.querySelector('#blazor-error-ui')`));

if (!COMMIT) {
  note('import, re-import and undo', 'E2E_COMMIT is not 1 — the suite stops before anything is written');
  t.close(); finish(); process.exit(fail ? 1 : 0);
}

// ---- E2E_COMMIT=1: answer everything, import, re-import, undo ---------------------------------------
async function answerEverything() {
  for (let guard = 0; guard < 400; guard++) {
    const clicked = await t.eval(`(() => {
      const li = document.querySelector('#pi-must-decide li.pi-issue');
      if (!li) return 'none';
      const buttons = [...li.querySelectorAll('button')].filter(b => !b.disabled);
      const pick = (re) => buttons.find(b => re.test(b.innerText.trim()));
      const b = li.querySelector('.pi-use-suggestion') || pick(/^Leave out$/) || pick(/^Leave it out$/) || pick(/^Leave the gap$/)
        || pick(/^Import as an event$/) || pick(/^Use /) || pick(/^Understood$/);
      if (!b) return 'stuck:' + li.innerText.slice(0, 120);
      b.click(); return 'ok';
    })()`);
    if (clicked === 'none') return true;
    if (clicked.startsWith('stuck')) { say(`    (could not answer: ${clicked})`); return false; }
    await t.sleep(250);
  }
  return false;
}

async function answerAttendance() {
  for (let i = 0; i < 100 && await t.eval(`document.querySelectorAll('.pi-attendance-empty button').length`) > 0; i++) {
    await t.eval(`document.querySelector('.pi-attendance-empty button').click(), 1`);
    await t.sleep(200);
  }
}

say('  4. Import, re-import, undo');
check('every question can be answered', await answerEverything());
// A meeting whose attendance matches nobody on this tenant's staff list is asked on its own row (found 2026-09-23:
// it used to be refused only at submit, after the page had said every question was answered).
const nobody = await t.eval(`document.querySelectorAll('.pi-attendance-empty').length`);
if (nobody > 0) {
  const gated = await t.eval(`document.querySelector('#pi-preview')?.disabled === true`);
  check('a meeting that would expect nobody holds "Check with the server" back', gated, `${nobody} such meeting(s)`);
  await answerAttendance();
  check('…and answering it ("an event only") releases it', await t.eval(`document.querySelector('#pi-preview')?.disabled === false`));
}
await t.clickText('Check with the server', '#pi-preview');
// A refusal is a toast that fades long before a two-minute wait ends, so read it straight after the press.
await t.sleep(1500);
const previewToast = await t.eval(`[...document.querySelectorAll('.q-toast, [class*=toast]')].map(e => e.innerText.trim()).filter(Boolean).join(' | ').slice(0, 600)`);
check('the server checks it and a single confirmation follows', await t.waitFor("!!document.querySelector('#pi-confirm-sentence')", 120000), previewToast);
const sentence = await t.eval(`document.querySelector('#pi-confirm-sentence')?.innerText ?? ''`);
check('the confirmation states what will be created', /Create /.test(sentence), sentence);
// Nobody on the dev tenant has a real mailbox, and a batch of unroutable emails starves the Hangfire queue
// (CLAUDE.md). The switch is the product's own way to import without announcing: turn it off unless asked.
if (process.env.E2E_NOTIFY !== '1' && await t.eval(`!!document.querySelector('.pi-notify input')`)) {
  await t.eval(`document.querySelector('.pi-notify input').click(), 1`);
  await t.sleep(400);
  const quiet = await t.eval(`document.querySelector('#pi-confirm-sentence')?.innerText ?? ''`);
  check('turning off "tell each person" takes the promise out of the sentence', !/tell \d+ (person|people)/.test(quiet), quiet);
}
await t.clickText('Import', '#pi-commit');
check('it imports', await t.waitFor("!!document.querySelector('#pi-done')", 180000), await t.eval('document.body.innerText.slice(0, 300)'));

// Re-import the same five documents: nothing new.
if (await chooseFiles()) {
  await t.sleep(800);
  await t.clickText('Continue', '#pi-continue');
  await t.waitFor("!!document.querySelector('#pi-must-decide')", 30000);
  await t.sleep(800);
  await answerEverything();
  await answerAttendance();
  await t.clickText('Check with the server', '#pi-preview');
  await t.waitFor("!!document.querySelector('#pi-confirm-sentence')", 120000);
  const again = await t.eval(`document.querySelector('#pi-confirm-sentence')?.innerText ?? ''`);
  check('re-importing the same documents creates nothing', /already here|Create nothing new/.test(again), again);
} else check('the second choose', false, 'the files could not be chosen again');

// Undo from the history.
await t.clickText('Import history', '#pi-history');
await t.waitFor("!!document.querySelector('#pi-jobs')", 20000);
await t.sleep(600);
await t.clickText('Undo', '#pi-jobs button');
await t.waitFor("!!document.querySelector('#pi-undo-confirm')", 10000);
await t.clickText('Undo the import', '#pi-undo-confirm');
check('undo reports what it removed', await t.waitFor("document.body.innerText.includes('Import undone')", 60000));

t.close();
finish();
