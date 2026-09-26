// THE SCHOOL'S WORD TEMPLATE, FILLED AND READ BACK (plan LESSON_PLANS_AND_SCHEMES_OF_WORK §5.2, 2026-09-26).
//
// The template is downloaded in the school's format (as a teacher would), filled in here the way Word would save it,
// and handed to the plan page's "Fill from a Word file" input. Its text must land IN THE FORM, for the teacher to check
// and save — the Word file itself is never stored. Done for a lesson plan and for a scheme of work's weeks.
//
//   node scripts/e2e/browser/plan-templates.mjs   (Chrome on 9333)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';
import { WEB, TP, api, account, seed, newPlan, press, unzip, zip, fillAfter, writeTemp } from './plan-fixture.mjs';

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
const made = [];

try {
  await login(t, M1.email, M1.password);

  // ---- A lesson plan ----
  const plan = await newPlan(M1, fx.subject, fx.classes[0], 800 + Math.floor(Math.random() * 200)); made.push(plan.id);
  const docx = await api(M1.token, 'GET', `${TP}/templates/lesson-plan.docx`);
  ok('the teacher downloads the school\'s lesson plan template', docx.status === 200 && docx.buf.slice(0, 2).toString() === 'PK');
  const parts = unzip(docx.buf);
  let xml = parts['word/document.xml'].toString('utf8');
  xml = fillAfter(xml, 'Topic', `E2E ${RUN} Angles in a triangle`);
  xml = fillAfter(xml, 'Learning outcomes', 'Learners find a missing angle (s).');
  xml = fillAfter(xml, 'Introduction', 'Measure the angles of a paper triangle', 1);
  parts['word/document.xml'] = Buffer.from(xml, 'utf8');
  const file = writeTemp(`filled-${RUN}.docx`, zip(parts));

  await t.goto(`${WEB}/plans/${plan.id}`);
  await t.waitFor(`!!document.getElementById('plan-docx-input')`, 20000);
  await t.setFiles('#plan-docx-input', [file]);
  const landed = await t.waitFor(`(document.querySelector('#plan-s-outcomes textarea, textarea#plan-s-outcomes')?.value ?? '').includes('missing angle')`, 20000);
  ok('the filled Word file is read INTO the form', landed);
  ok('…the topic too', await t.eval(`(document.querySelector('#plan-s-topic textarea, textarea#plan-s-topic')?.value ?? '').includes('Angles in a triangle')`));
  ok('…and the procedure\'s teacher activity', await t.eval(`[...document.querySelectorAll('#plan-procedure textarea')].some(t => t.value.includes('paper triangle'))`));
  await press(t, `document.getElementById('plan-save')`);
  await t.sleep(1500);
  const saved = (await api(M1.token, 'GET', `${TP}/${plan.id}`)).json;
  ok('saving keeps what was read — and there is no stored file', saved?.content?.answers?.outcomes?.includes('missing angle') && !saved?.fileUrl, JSON.stringify(saved?.content?.answers));

  // ---- A scheme of work ----
  let scheme = (await api(M1.token, 'POST', TP, { kind: 'SchemeOfWork', subjectId: fx.subject.id, classNames: [fx.classes[0], fx.classes[1]], clientRequestId: crypto.randomUUID() })).json;
  if (scheme?.status === 'Approved') scheme = (await api(M1.token, 'POST', `${TP}/${scheme.id}/revise`)).json;
  if (scheme?.status === 'Submitted' || scheme?.status === 'Forwarded') scheme = (await api(M1.token, 'POST', `${TP}/${scheme.id}/withdraw`)).json;
  const sdoc = await api(M1.token, 'GET', `${TP}/templates/scheme-of-work.docx?subjectId=${fx.subject.id}`);
  const sparts = unzip(sdoc.buf);
  let sxml = sparts['word/document.xml'].toString('utf8');
  ok('the scheme template is landscape, with the school\'s columns', sxml.includes('w:orient="landscape"') && sxml.includes('Learning outcomes'));
  sxml = fillAfter(sxml, '1</w:t>', `E2E ${RUN} Geometry`, 1);          // week 1: skip Periods → Topic
  sxml = fillAfter(sxml, '1</w:t>', 'Learners classify angles (k)', 3); // …then Sub-topic and Competency → Outcomes
  sparts['word/document.xml'] = Buffer.from(sxml, 'utf8');
  await t.goto(`${WEB}/plans/${scheme.id}`);
  await t.waitFor(`!!document.getElementById('plan-docx-input')`, 20000);
  await t.setFiles('#plan-docx-input', [writeTemp(`scheme-${RUN}.docx`, zip(sparts))]);
  const rowIn = await t.waitFor(`[...document.querySelectorAll('#plan-scheme textarea')].some(t => t.value.includes('Geometry'))`, 20000);
  ok('the scheme\'s weeks are read into its table', rowIn);
  ok('…with the outcomes in the outcomes column', await t.eval(`[...document.querySelectorAll('#plan-scheme textarea')].some(t => t.value.includes('classify angles'))`));
  ok('no error bar', await t.eval(`getComputedStyle(document.getElementById('blazor-error-ui')).display === 'none'`));
} catch (e) {
  ok('the suite ran to the end', false, e?.stack ?? String(e));
} finally {
  for (const id of made) await api(M1.token, 'POST', `${TP}/${id}/discard`);
  await fx.undo();
  console.log(`\n${pass} passed, ${fail} failed`);
  process.exitCode = fail ? 1 : 0;
  t.close?.();
}
