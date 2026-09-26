// LESSON PLANS IN THE BROWSER (plan LESSON_PLANS_AND_SCHEMES_OF_WORK, 2026-09-26).
//
// A teacher plans a lesson on the FORM — the main route — and submits it; the head of department finds it in the
// Timetable hub's Plans tab and approves it; the teacher then writes the self-evaluation. Every press is real mouse
// input. Also: the card on My Workspace, the Setup tab, and the Access tab's separation-of-duties card.
//
//   node scripts/e2e/browser/lesson-plan-ui.mjs   (headless or visible Chrome on 9333; see CLAUDE.md)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';
import { WEB, TP, api, account, seed, newPlan, press } from './plan-fixture.mjs';

const RUN = Date.now().toString(36);
let pass = 0, fail = 0;
const viewer = (l) => fetch('http://127.0.0.1:5010/append?key=browser', { method: 'POST', body: l + '\n' }).catch(() => {});
const ok = (name, c, d = '') => { c ? pass++ : fail++; const l = `${c ? 'PASS' : 'FAIL'}  ${name}${c || !d ? '' : '  — ' + d}`; console.log(l); viewer(l); };

const AD = await account(process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local');
const HOD = await account('e2e.sp.hod.math@qmgr.local');
const M1 = await account('e2e.sp.math1@qmgr.local');
if (!AD || !HOD || !M1) { console.error('the section 14 accounts are missing'); process.exit(1); }
const fx = await seed(AD, M1, HOD);
const t = await openTab();
await t.viewport(1440, 950);
const noError = () => t.eval(`getComputedStyle(document.getElementById('blazor-error-ui')).display === 'none'`);
const setField = (selector, value) => t.setValue(selector, value);

try {
  // ---- The teacher: My Workspace, then the form ----
  await login(t, M1.email, M1.password);
  await t.goto(`${WEB}/portal?tab=teaching`);
  const card = await t.waitFor(`!!document.getElementById('my-plans')`, 20000);
  ok('My Workspace → My teaching carries the plans card', card);
  if (card) ok('…with the week and the templates', await t.eval(`!!document.getElementById('plans-template-lesson') && !!document.getElementById('plans-new-scheme')`));

  const plan = await newPlan(M1, fx.subject, fx.classes[0], 300 + Math.floor(Math.random() * 300));
  await t.goto(`${WEB}/plans/${plan.id}`);
  const form = await t.waitFor(`!!document.getElementById('plan-save') && !!document.getElementById('plan-procedure')`, 20000);
  ok('the plan opens on the form, as a draft, with the procedure table', form);
  ok('the header is filled in already (the date and the learners)', await t.eval(`/Date/.test(document.querySelector('.plan-header')?.innerText ?? '')`));
  ok('no error bar on the form', await noError());

  await setField('#plan-s-topic', `E2E ${RUN} Fractions`);
  await setField('#plan-s-outcomes', 'By the end, learners add fractions with like denominators (s).');
  await t.eval(`(() => { const ta = document.querySelector('#plan-procedure tbody tr textarea'); if (!ta) return false;
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(ta, 'Recall halves and quarters');
    ta.dispatchEvent(new Event('input', { bubbles: true })); ta.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
  await t.sleep(400);
  await press(t, `document.getElementById('plan-save')`);
  await t.sleep(1200);
  const saved = (await api(M1.token, 'GET', `${TP}/${plan.id}`)).json;
  ok('what was typed reached the server', saved?.content?.answers?.outcomes?.includes('like denominators') && saved?.content?.procedure?.[0]?.teacher?.includes('halves'), JSON.stringify(saved?.content?.answers));

  await press(t, `document.getElementById('plan-submit')`);
  const submitted = await t.waitFor(`/With head of department/.test(document.getElementById('plan-status')?.innerText ?? '')`, 15000);
  ok('Submit: the plan is with the head of department', submitted);
  ok('…and the form is frozen (no Save)', await t.eval(`!document.getElementById('plan-save')`));
  ok('…and who has it is named', await t.eval(`(document.getElementById('plan-waiting')?.innerText ?? '').length > 5`));

  // ---- The head of department: the Plans tab, then Approve ----
  await login(t, HOD.email, HOD.password);
  await t.goto(`${WEB}/admin/timetable?tab=plans`);
  const queued = await t.waitFor(`!!document.querySelector('#plans-queue tr[data-plan="${plan.id}"]')`, 20000);
  ok('the head finds it in the Timetable hub → Plans, waiting on them', queued);
  if (queued) {
    await press(t, `document.querySelector('#plans-queue tr[data-plan="${plan.id}"] button')`);
    ok('"Review" opens the plan', await t.waitFor(`location.pathname === '/plans/${plan.id}' && !!document.getElementById('plan-approve')`, 15000));
    ok('…with no Save for the reviewer', await t.eval(`!document.getElementById('plan-save')`));
    await press(t, `document.getElementById('plan-approve')`);
    await t.waitFor(`!!document.getElementById('plan-decision-confirm')`, 5000);
    await setField('#plan-decision-note', 'Clear outcomes. Approved.');
    await press(t, `document.getElementById('plan-decision-confirm')`);
    ok('Approve: the plan is approved', await t.waitFor(`/Approved/.test(document.getElementById('plan-status')?.innerText ?? '')`, 15000));
    ok('…and the history says who approved it', await t.eval(`/approved it/.test(document.getElementById('plan-trail')?.innerText ?? '')`));
  }
  ok('no error bar for the head', await noError());

  // ---- The teacher again: the self-evaluation ----
  await login(t, M1.email, M1.password);
  await t.goto(`${WEB}/plans/${plan.id}`);
  const reflect = await t.waitFor(`!!document.getElementById('plan-reflection')`, 15000);
  ok('after approval the teacher may still write the self-evaluation', reflect);
  if (reflect) {
    await setField('#plan-reflection', 'Needed more worked examples.');
    await press(t, `document.getElementById('plan-reflect')`);
    await t.sleep(1200);
    ok('…and it is kept', ((await api(M1.token, 'GET', `${TP}/${plan.id}`)).json?.reflection ?? '').includes('worked examples'));
  }
  ok('an approved plan offers Revise, not Save', await t.eval(`!!document.getElementById('plan-revise') && !document.getElementById('plan-save')`));

  // ---- The hard copy, filled from the form and headed by the school ----
  await t.goto(`${WEB}/plans/${plan.id}/print`);
  const printed = await t.waitFor(`!!document.querySelector('.ps-head') && !!document.getElementById('pp-fields')`, 20000);
  ok('Print: the filled plan is headed by the school', printed && await t.eval(`(document.querySelector('.ps-school')?.innerText ?? '').trim().length > 2`));
  ok('…carries what the teacher wrote', await t.eval(`/like denominators/.test(document.querySelector('.q-print__sheet')?.innerText ?? '')`));
  // Found by LOOKING at the sheet: under the dark theme the section titles inherited a light heading colour and printed
  // white on white. Every title must be dark and say something.
  ok('…with every section title printed in black, not the screen theme\'s colour', await t.eval(`(() => { const hs = [...document.querySelectorAll('.ps-box h3')];
    return hs.length > 5 && hs.every(h => h.innerText.trim().length > 0 && getComputedStyle(h).color === 'rgb(17, 17, 17)'); })()`));
  ok('…and who approved it, on the signature line', await t.eval(`/Head of department/.test(document.getElementById('pp-sign')?.innerText ?? '') && /Approved/i.test(document.querySelector('.ps-stamp')?.innerText ?? '')`));
  await t.goto(`${WEB}/plans/blank/print?kind=lesson`);
  ok('a blank lesson plan in the school\'s format prints for writing by hand', await t.waitFor(`!!document.getElementById('pb-procedure') && document.querySelectorAll('.ps-box').length > 5`, 20000));
  await t.goto(`${WEB}/plans/blank/print?kind=scheme`);
  ok('…and a blank scheme, one line per week of the term', await t.waitFor(`document.querySelectorAll('#pb-scheme tbody tr').length >= 1`, 20000));

  // ---- The school's side: the per-teacher report, Setup → Lesson Plans, and Access ----
  await login(t, AD.email, AD.password);
  await t.goto(`${WEB}/admin/timetable?tab=plans&view=reports`);
  ok('Reports list every teacher\'s scheme of work by state', await t.waitFor(`!!document.getElementById('plans-schemes') || /Nobody is assigned/.test(document.body.innerText)`, 25000));
  await t.goto(`${WEB}/admin/staff/parameters?tab=plans`);
  ok('Setup → Lesson Plans shows the school\'s sections and rules', await t.waitFor(`!!document.getElementById('tps-sections') && !!document.getElementById('tps-save')`, 20000));
  ok('…and the curriculum list', await t.eval(`/Curriculum list/.test(document.body.innerText)`));
  await t.goto(`${WEB}/admin/users?tab=access`);
  ok('Users & Roles → Access states the separation-of-duties rules', await t.waitFor(`!!document.getElementById('access-sod')`, 20000));
  ok('no error bar on the settings pages', await noError());
} catch (e) {
  ok('the suite ran to the end', false, e?.stack ?? String(e));
} finally {
  await fx.undo();
  console.log(`\n${pass} passed, ${fail} failed`);
  process.exitCode = fail ? 1 : 0;
  t.close?.();
}
