// A PLAN'S PDF IS SHRUNK IN THE BROWSER (plan LESSON_PLANS_AND_SCHEMES_OF_WORK §5.4, 2026-09-26).
//
// Real files go into the plan page's own file input (CDP DOM.setFileInputFiles — the page's JavaScript does the work, as
// it would for a teacher):
//   * a text PDF bloated the way Word bloats one (200 KB of dead weight) comes out under the school's 50 KB target, the
//     teacher sees before and after, and the server keeps the small one;
//   * a SCAN (one grey picture, no text) is turned black-and-white and still fits;
//   * too many pages, and a file that is not a PDF, are refused on the spot, in words, before anything is sent.
//
//   node scripts/e2e/browser/plan-pdf.mjs   (Chrome on 9333; loads pdf.js and pdf-lib from jsDelivr on demand)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';
import { WEB, TP, api, account, seed, newPlan, press, textPdf, scanPdf, writeTemp } from './plan-fixture.mjs';

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

async function choose(planId, file) {
  await t.goto(`${WEB}/plans/${planId}`);
  await t.waitFor(`!!document.getElementById('plan-pdf-input')`, 20000);
  await t.setFiles('#plan-pdf-input', [file]);
  // pdf.js and pdf-lib load from the CDN the first time: allow for it.
  return t.waitFor(`!!document.getElementById('plan-shrink-summary') || !!document.getElementById('plan-shrink-problem')`, 60000);
}

try {
  await login(t, M1.email, M1.password);

  // ---- 1. A bloated text PDF ----
  const bloated = textPdf({ pad: 200 * 1024 });
  const p1 = await newPlan(M1, fx.subject, fx.classes[0], 400 + Math.floor(Math.random() * 200)); made.push(p1.id);
  ok('a bloated Word-style PDF is read in the browser', await choose(p1.id, writeTemp('bloated.pdf', bloated)));
  const summary = await t.eval(`document.getElementById('plan-shrink-summary')?.innerText ?? document.getElementById('plan-shrink-problem')?.innerText ?? ''`);
  ok('…and the teacher sees the size before and after', /→/.test(summary), summary);
  ok('…with both pages side by side', await t.eval(`document.querySelectorAll('.plan-previews img').length === 2`));
  await press(t, `document.getElementById('plan-attach')`);
  await t.waitFor(`!!document.getElementById('plan-file')`, 20000);
  const stored1 = (await api(M1.token, 'GET', `${TP}/${p1.id}`)).json;
  ok('the server keeps the shrunk file, under the 50 KB target', stored1?.fileSizeBytes > 0 && stored1.fileSizeBytes < 50 * 1024, `${stored1?.fileSizeBytes} bytes`);
  ok('…and records how big the original was', stored1?.originalSizeBytes >= bloated.length, `${stored1?.originalSizeBytes} vs ${bloated.length}`);
  ok('…and the page says it was shrunk', await t.eval(`/shrunk from/.test(document.getElementById('plan-file')?.innerText ?? '')`));
  const file = await fetch(stored1?.fileUrl ?? '');
  const bytes = Buffer.from(await file.arrayBuffer());
  ok('the stored file is a real PDF with none of the dead weight', bytes.slice(0, 5).toString() === '%PDF-' && !bytes.includes(Buffer.from('x'.repeat(1000))), `${bytes.length} bytes`);

  // ---- 2. A scan ----
  const scan = scanPdf();
  const p2 = await newPlan(M1, fx.subject, fx.classes[1], 620 + Math.floor(Math.random() * 100)); made.push(p2.id);
  ok(`a scan (${Math.round(scan.length / 1024)} KB of grey picture, no text) is read`, await choose(p2.id, writeTemp('scan.pdf', scan)));
  const scanSummary = await t.eval(`(document.getElementById('plan-shrink-summary')?.innerText ?? '') + ' ' + [...document.querySelectorAll('.q-modal .cell-sub')].map(e => e.innerText).join(' ')`);
  ok('…it is kept as black-and-white and the teacher is told', /black-and-white/i.test(scanSummary) && /→/.test(scanSummary), scanSummary.slice(0, 200));
  await press(t, `document.getElementById('plan-attach')`);
  await t.waitFor(`!!document.getElementById('plan-file')`, 20000);
  const stored2 = (await api(M1.token, 'GET', `${TP}/${p2.id}`)).json;
  ok('…and fits within the school\'s 64 KB limit', stored2?.fileSizeBytes > 0 && stored2.fileSizeBytes <= 64 * 1024, `${stored2?.fileSizeBytes} bytes from ${scan.length}`);
  ok('…at a fraction of the photograph\'s size', stored2?.fileSizeBytes * 5 < scan.length, `${Math.round((stored2?.fileSizeBytes ?? 0) / 1024)} KB from ${Math.round(scan.length / 1024)} KB`);

  // ---- 3. Refused on the spot ----
  const p3 = await newPlan(M1, fx.subject, fx.classes[0], 760 + Math.floor(Math.random() * 100)); made.push(p3.id);
  await choose(p3.id, writeTemp('six.pdf', textPdf({ pages: 6 })));
  ok('six pages are refused before anything is sent — the school allows four', /6 pages/.test(await t.eval(`document.getElementById('plan-shrink-problem')?.innerText ?? ''`)));
  ok('…and nothing is offered to attach', await t.eval(`!document.getElementById('plan-attach')`));
  await press(t, `[...document.querySelectorAll('.q-modal button')].find(b => /Cancel/.test(b.innerText))`);
  await choose(p3.id, writeTemp('fake.pdf', Buffer.from('this is not a PDF at all, only text')));
  ok('a file that is not a PDF is refused, in words', /could not be opened/i.test(await t.eval(`document.getElementById('plan-shrink-problem')?.innerText ?? ''`)));
  ok('no error bar anywhere', await t.eval(`getComputedStyle(document.getElementById('blazor-error-ui')).display === 'none'`));
} catch (e) {
  ok('the suite ran to the end', false, e?.stack ?? String(e));
} finally {
  for (const id of made) await api(M1.token, 'POST', `${TP}/${id}/discard`);
  await fx.undo();
  console.log(`\n${pass} passed, ${fail} failed`);
  process.exitCode = fail ? 1 : 0;
  t.close?.();
}
