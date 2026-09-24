// The four timetable screens section 26 cannot see (2026-09-23).
//
// Timetable ownership and cover/swap were verified by the API suite (section 26, 54 checks) the day they were
// built, and nothing in a browser opened the screens a person uses: the appoint-a-master dialog, the override
// notice, the replace-a-live-version confirmation, and "Ask a colleague" on My Workspace. That last one is the
// argument for this suite on its own: the swap journey existed in the API for a day and was unreachable,
// because nothing in the app ever posted MyLessonId — a class of fault a curl suite cannot see by construction.
//
// Everything it needs it makes, and puts back: a published week in force today (seed-timetable.mjs, the one home
// for that), two drafts copied from it, the school's self-service switch, and the cover request it sends.
//
// Run: node scripts/e2e/browser/timetable-ownership-ui.mjs   (headless Chrome on 9333; see CLAUDE.md)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';
import { seedLiveTimetable } from './seed-timetable.mjs';

const BASE = process.env.BASE ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';
const TEACHER = process.env.E2E_TEACHER_USER ?? 'e2e.sp.math1@qmgr.local';
const TEACHER_PASSWORDS = process.env.E2E_TEACHER_PASS ? [process.env.E2E_TEACHER_PASS] : ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];
const RUN = Date.now().toString(36);

let pass = 0, fail = 0;
const post = (l) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: l + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const hdr = (s) => { console.log(`\n${s}`); post(`\n${s}`); };

const signIn = async (email, password) => (await fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }),
}).then(r => r.json()).catch(() => ({}))).accessToken;
const api = async (token, method, path, body) => {
  const r = await fetch(`${API}${path}`, {
    method, headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await r.text(); let json = null; try { json = text ? JSON.parse(text) : null; } catch { }
  return { status: r.status, json, text };
};
const claims = (jwt) => JSON.parse(Buffer.from(jwt.split('.')[1], 'base64').toString());

const AD = await signIn(USER, PASS);
if (!AD) { console.error('could not sign in as the administrator'); process.exit(1); }
let teacherPass = null, TE = null;
for (const pw of TEACHER_PASSWORDS) { TE = await signIn(TEACHER, pw); if (TE) { teacherPass = pw; break; } }
if (!TE) { console.error(`could not sign in as ${TEACHER}`); process.exit(1); }
const teacherId = claims(TE).sub;
const TT = `/api/v1/branches/${BRANCH}/timetable`;
const SS = `/api/v1/branches/${BRANCH}/staff/self-service`;

hdr(`== Timetable ownership and cover, in the browser (run ${RUN}) ==`);

// The swap card renders only while the school has staff self-service switched on. Put it back as found.
const policyBefore = (await api(AD, 'GET', '/api/v1/staff/policy')).json;
const policyOn = JSON.parse(JSON.stringify(policyBefore));
policyOn.selfService = { ...(policyOn.selfService ?? {}), enabled: true, allowRequests: true };
await api(AD, 'PUT', '/api/v1/staff/policy', policyOn);

const tt = await seedLiveTimetable({ API, BRANCH, token: AD, teacherId, label: 'own' });
const sentRequests = [];
const t = await openTab();
await t.viewport(1440, 950);

const center = (expr) => t.eval(`(() => { const e = ${expr}; if (!e) return null;
  e.scrollIntoView({ block: 'center', behavior: 'instant' });
  const r = e.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
async function press(p, hold = 80) {
  if (!p) return false;
  await t.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: p.x, y: p.y });
  await t.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  await t.sleep(hold);
  await t.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  return true;
}
const button = (text, scope = 'document') => `[...${scope}.querySelectorAll('button')].find(b => b.offsetParent !== null && b.innerText.trim() === ${JSON.stringify(text)})`;
const modal = (title) => `[...document.querySelectorAll('.q-modal')].find(m => m.offsetParent !== null && (m.querySelector('.q-modal__title')?.innerText ?? m.innerText).includes(${JSON.stringify(title)}))`;
const noErrorBar = async () => (await t.eval(`(() => { const e = document.querySelector('#blazor-error-ui'); return e ? getComputedStyle(e).display : 'none'; })()`)) === 'none';
const writeIn = (scopeExpr, value) => t.eval(`(() => { const el = (${scopeExpr})?.querySelector('textarea'); if (!el) return false;
  const set = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set; set.call(el, ${JSON.stringify(value)});
  el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
const pointAtBranch = () => t.eval(`localStorage.setItem('qmgr-branch', ${JSON.stringify(JSON.stringify(BRANCH))}); true`);

try {
  if (!tt.target) {
    const l = `    SKIP  no published week could be put in force today (${tt.reason})`;
    console.log(l); post(l);
  } else {
    const names = new Map(tt.lessons.map(l => [l.teacherUserId, l.teacherName]));
    const teacherName = names.get(teacherId);
    const colleagueId = [...names.keys()].find(id => id !== teacherId);
    const colleagueName = names.get(colleagueId);

    // Two drafts copied from the live week: one far from today for the owner screens, one over the SAME dates
    // for the replace confirmation. A random offset, because a draft left by an interrupted run must not collide.
    const far = new Date(); far.setDate(far.getDate() + 500 + Math.floor(Math.random() * 300));
    const farEnd = new Date(far); farEnd.setDate(farEnd.getDate() + 6);
    const iso = (d) => d.toISOString().slice(0, 10);
    const ownDraft = await tt.copyDraft(tt.target.id, `E2E own ${RUN}`, iso(far), iso(farEnd));
    const overDraft = await tt.copyDraft(tt.target.id, `E2E replace ${RUN}`, tt.from, tt.to);
    check('0a: two drafts are made from the live week', !!ownDraft.id && !!overDraft.id, `${ownDraft.error ?? ''} ${overDraft.error ?? ''}`);

    await login(t, USER, PASS);
    await pointAtBranch();

    // ------------------------------------------------------------------------------ 1. appoint a master
    hdr('1. Appointing a master');
    await t.goto(`${BASE}/admin/timetable?t=${ownDraft.id}`);
    await t.waitFor(`!!${button('Appoint a master')}`, 30000);
    check('1a: an administrator is offered "Appoint a master" on an unowned draft', await t.eval(`!!${button('Appoint a master')}`));
    await press(await center(button('Appoint a master')));
    const opened = await t.waitFor(`!!${modal('Who builds this timetable')}`, 8000);
    check('1b: it opens "Who builds this timetable"', !!opened);

    await press(await center(`${modal('Who builds this timetable')}?.querySelector('.q-multiselect')`));
    await t.waitFor(`!!document.querySelector('.q-multiselect__dropdown')`, 5000);
    await t.setValue('.q-multiselect__filter-input', teacherName.split(' ')[0]);
    await t.sleep(600);
    const optionFor = `[...document.querySelectorAll('.q-multiselect__dropdown .q-select__option')].find(o => o.innerText.trim() === ${JSON.stringify(teacherName)})`;
    const picked = await press(await center(optionFor));
    check('1c: the teacher can be picked in the list (a real press, not element.click)', picked, `no option reading "${teacherName}"`);
    await t.sleep(300);
    await press(await center(`${modal('Who builds this timetable')}?.querySelector('.q-modal__title, h2, h3')`)); // close the list
    await t.sleep(300);
    await press(await center(button('Save', modal('Who builds this timetable'))));
    await t.waitFor(`!${modal('Who builds this timetable')}`, 8000);
    await t.sleep(800);
    // textContent, not innerText: a chip may be styled uppercase, and innerText returns what is DRAWN.
    const chip = await t.eval(`[...document.querySelectorAll('.tt-version-meta .q-chip')].map(c => c.textContent.trim()).find(x => /^master:/i.test(x)) ?? ''`);
    check('1d: the version now says whose it is, on the line that says what it is', chip.includes(teacherName), `chip "${chip}"`);
    const saved = (await api(AD, 'GET', `${TT}/timetables/${ownDraft.id}`)).json;
    check('1e: and the server holds the appointment', (saved?.timetable?.managerUserIds ?? []).includes(teacherId),
      JSON.stringify(saved?.timetable?.managerUserIds ?? null));
    check('1f: the button now reads "Change master"', await t.eval(`!!${button('Change master')}`));

    // ------------------------------------------------------------------------------ 2. the override notice
    hdr('2. The override notice');
    const note = await t.eval(`document.querySelector('.tt-override')?.innerText.trim() ?? ''`);
    check('2a: an administrator editing somebody else\'s timetable is told BEFORE touching it', note.length > 0, 'no .tt-override');
    check('2b: …naming the master, and saying they will be told', note.includes(teacherName) && /will be told/.test(note), `"${note}"`);
    check('2c: no error bar', await noErrorBar());

    // ------------------------------------------------------------------------------ 3. replacing a live version
    hdr('3. Publishing over a live version asks first');
    await t.goto(`${BASE}/admin/timetable?t=${overDraft.id}`);
    await t.waitFor(`!!${button('Publish')}`, 30000);
    await press(await center(button('Publish')));
    // A copy carries the live week's acknowledged soft clashes, so that question comes first. Its Publish button is
    // disabled until the reason is written — correct, and the suite writes one.
    let softState = '';
    // waitFor answers true/false, never the expression's value: wait for EITHER dialog, then ask which it is.
    await t.waitFor(`!!${modal('Publish with soft clashes')} || !!${modal('Another timetable is already live')}`, 15000);
    const first = await t.eval(`${modal('Publish with soft clashes')} ? 'soft' : 'replace'`);
    if (first === 'soft') {
      await writeIn(modal('Publish with soft clashes'), `E2E ${RUN} soft clashes copied from the live week`);
      await t.sleep(500);
      softState = await t.eval(`(() => { const m = ${modal('Publish with soft clashes')}; const ta = m?.querySelector('textarea'); const b = [...(m?.querySelectorAll('button') ?? [])].find(x => x.innerText.trim() === 'Publish'); return JSON.stringify({ note: ta?.value?.slice(0, 30), disabled: b?.disabled ?? 'no button' }); })()`);
      await press(await center(button('Publish', modal('Publish with soft clashes'))));
      await t.sleep(2500);
      softState += ' after press: ' + await t.eval(`[...document.querySelectorAll('.q-modal__title')].filter(x => x.offsetParent !== null).map(x => x.innerText).join(' | ') + ' / ' + [...document.querySelectorAll('.q-toast')].map(x => x.innerText.replace(/\s+/g, ' ')).join(' || ')`);
      await t.waitFor(`!!${modal('Another timetable is already live')}`, 15000);
    }
    const replaceText = await t.eval(`${modal('Another timetable is already live')}?.innerText ?? ''`);
    check('3a: publishing a draft whose dates are covered by a live version opens "Another timetable is already live"', replaceText.length > 0,
      await t.eval(`[...document.querySelectorAll('.q-modal__title')].filter(x => x.offsetParent !== null).map(x => x.innerText).join(' | ') + ' / ' + (document.querySelector('.q-toast')?.innerText ?? 'no toast')`) + ' — soft step: ' + softState);
    if (!replaceText) throw new Error('the replace confirmation never opened');
    check('3b: …naming the live version it would take out of service', replaceText.includes(tt.target.name), replaceText.slice(0, 200));
    check('3c: …and saying what that does to registers and teachers\' days', /registers/.test(replaceText) && /every teacher/.test(replaceText), replaceText.slice(0, 300));
    await press(await center(button('Cancel', modal('Another timetable is already live'))));
    await t.sleep(1200);
    const afterCancel = (await api(AD, 'GET', `${TT}/timetables/${overDraft.id}`)).json?.timetable?.status;
    const liveAfter = (await api(AD, 'GET', `${TT}/timetables/${tt.target.id}`)).json?.timetable?.status;
    check('3d: Cancel publishes nothing: the draft is still a draft', afterCancel === 'Draft', afterCancel);
    check('3e: …and the live version is still live', liveAfter === 'Published', liveAfter);

    // ------------------------------------------------------------------------------ 4. the master, and "Ask a colleague"
    hdr('4. The appointed master, and asking a colleague');
    await login(t, TEACHER, teacherPass);
    await pointAtBranch();
    await t.goto(`${BASE}/admin/timetable?t=${ownDraft.id}`);
    await t.waitFor(`!!document.querySelector('.tt-version-meta')`, 30000);
    await t.sleep(800);
    check('4a: the appointed master can open their draft with Publish offered — without the permission', await t.eval(`!!${button('Publish')}`));
    check('4b: …and is not warned about overriding themselves', !(await t.eval(`!!document.querySelector('.tt-override')`)));
    check('4c: …and cannot appoint anybody (that stays with an administrator)',
      !(await t.eval(`!!${button('Change master')} || !!${button('Appoint a master')}`)));

    await t.goto(`${BASE}/portal?tab=teaching`);
    const card = `[...document.querySelectorAll('.q-card')].find(c => c.innerText.includes('Ask a colleague'))`;
    await t.waitFor(`!!${card} && !!${card}.querySelector('tbody tr')`, 30000);
    const rows = await t.eval(`${card}?.querySelectorAll('tbody tr').length ?? 0`);
    check('4d: "Ask a colleague" lists the teacher\'s own lessons on the live week', rows > 0, `${rows} rows`);

    await press(await center(`[...${card}.querySelectorAll('button')].find(b => b.innerText.trim() === 'Ask to swap')`));
    await t.waitFor(`!!${modal('Ask to swap a lesson')}`, 8000);
    await press(await center(`${modal('Ask to swap a lesson')}?.querySelector('.q-select')`));
    await t.waitFor(`!!document.querySelector('.q-select__dropdown')`, 5000);
    await t.sleep(300);
    const swapOptions = await t.eval(`[...document.querySelectorAll('.q-select__dropdown .q-select__option:not(.q-select__option--clear)')].map(o => o.innerText.trim())`);
    check('4e: "Ask to swap" offers the colleague\'s lessons to take in exchange', swapOptions.length > 0 && swapOptions.some(o => o.includes(colleagueName.split(' ')[0])),
      JSON.stringify(swapOptions.slice(0, 4)));
    await t.eval(`document.activeElement?.blur(); true`);
    await press(await center(`${modal('Ask to swap a lesson')}?.querySelector('.q-modal__title, h2, h3')`));
    await t.sleep(300);
    await press(await center(button('Cancel', modal('Ask to swap a lesson'))));
    await t.sleep(600);

    await press(await center(`[...${card}.querySelectorAll('button')].find(b => b.innerText.trim() === 'Ask for cover')`));
    await t.waitFor(`!!${modal('Ask a colleague to cover it')}`, 8000);
    await press(await center(`${modal('Ask a colleague to cover it')}?.querySelector('.q-select')`));
    await t.waitFor(`!!document.querySelector('.q-select__dropdown')`, 5000);
    await t.sleep(300);
    const colleagueOption = `[...document.querySelectorAll('.q-select__dropdown .q-select__option')].find(o => o.innerText.trim() === ${JSON.stringify(colleagueName)})`;
    check('4f: the colleague can be chosen to cover it', await press(await center(colleagueOption)), `no option "${colleagueName}"`);
    await t.sleep(400);
    await writeIn(modal('Ask a colleague to cover it'), `E2E ${RUN} cover test — safe to ignore`);
    const before = new Set(((await api(TE, 'GET', `${SS}/requests`)).json ?? []).map(r => r.id));
    await press(await center(button('Send the request', modal('Ask a colleague to cover it'))));
    const sent = await t.waitFor(`!${modal('Ask a colleague to cover it')}`, 10000);
    const mine = ((await api(TE, 'GET', `${SS}/requests`)).json ?? []).filter(r => !before.has(r.id));
    for (const r of mine) sentRequests.push(r.id);
    check('4g: "Send the request" sends it — the dialog closes', !!sent,
      await t.eval(`document.querySelector('.q-modal .form-error')?.innerText ?? ''`));
    check('4h: …and the server holds ONE new cover request, waiting on the colleague', mine.length === 1 && mine[0].kind === 'LessonCover',
      JSON.stringify(mine.map(r => [r.kind, r.state])));
    check('4i: no error bar anywhere on the teacher\'s side', await noErrorBar());
  }
} catch (e) {
  check('the suite ran to the end', false, e.stack?.slice(0, 300) ?? String(e));
} finally {
  for (const id of sentRequests) {
    const r = await api(TE, 'POST', `${SS}/requests/${id}/withdraw`, {});
    console.log(`    cleanup: withdrew cover request ${id.slice(0, 8)} (${r.status})`);
  }
  await tt.cleanup();
  await api(AD, 'PUT', '/api/v1/staff/policy', { ...(await api(AD, 'GET', '/api/v1/staff/policy')).json, selfService: policyBefore.selfService });
  console.log('    cleanup: self-service switched back as it was');
  t.close();
}

console.log(`\n${pass} passed, ${fail} failed`);
post(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
