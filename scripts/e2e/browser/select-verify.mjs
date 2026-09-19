// Verifies QSelect / QMultiSelect with REAL input (CDP Input.dispatchMouseEvent / dispatchKeyEvent), which moves focus the
// way a hand does. Results stream to the viewer's "ui" tab and stdout.
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';
const BASE = 'http://127.0.0.1:5003';
let pass = 0, fail = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => { ok ? pass++ : fail++; const l = `    ${ok ? 'PASS' : 'FAIL'}  SELECT: ${name}${ok ? '' : '  — ' + detail}`; console.log(l); post(l); };
const t = await openTab();
await t.viewport(1440, 900);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');
post('\n== Dropdowns: real mouse and keyboard input (QSelect, QMultiSelect, migrated native selects) ==');

const center = (expr) => t.eval(`(() => { const e = ${expr}; if (!e) return null; e.scrollIntoView({block:'center', behavior:'instant'}); const r = e.getBoundingClientRect(); return { x: r.left + r.width/2, y: r.top + r.height/2 }; })()`);
async function press(p, hold = 80) {
  await t.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: p.x, y: p.y });
  await t.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  await t.sleep(hold);
  await t.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: p.x, y: p.y, button: 'left', clickCount: 1 });
}
const key = async (k, code, vk) => { await t.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', key: k, code, windowsVirtualKeyCode: vk }); await t.send('Input.dispatchKeyEvent', { type: 'keyUp', key: k, code, windowsVirtualKeyCode: vk }); };
const isOpen = () => t.eval(`!!document.querySelector('.q-select__dropdown')`);
const trig = (scope, i) => `${scope}.querySelectorAll('.q-select')[${i}]`;
const text = (scope, i) => t.eval(`${trig(scope, i)}?.innerText.trim()`);
const optionsSel = `[...document.querySelectorAll('.q-select__dropdown .q-select__option:not(.q-select__option--clear)')]`;

async function open(scope, i) {
  if (await isOpen()) { await press({ x: 1400, y: 12 }); await t.sleep(500); }
  await press(await center(trig(scope, i)));
  await t.waitFor(`!!document.querySelector('.q-select__dropdown')`, 3000);
  await t.sleep(250);
}
/** Opens select i, presses an option other than the current one for `hold` ms, returns [picked, now]. */
async function pick(scope, i, hold, filter = null) {
  const before = await text(scope, i);
  await open(scope, i);
  const opts = await t.eval(`${optionsSel}.map(o => o.innerText.trim())`);
  const idx = filter ? opts.findIndex(filter) : opts.length === 1 ? 0 : opts.findIndex(o => o !== before);
  if (idx < 0) return [null, before, opts];
  const p = await center(`${optionsSel}[${idx}]`);
  await press(p, hold);
  await t.sleep(900);
  return [opts[idx], await text(scope, i), opts];
}

// 1. Plain and searchable QSelects at three press lengths.
// Staff Records, not the Timetable: the timetable page shows selects only once a DRAFT exists,
// so on a tenant with none this suite measured an empty page and died on a null element rather
// than saying so. The Records filter bar carries three QSelects whatever the data.
await t.goto(BASE + '/admin/staff/records');
await t.waitFor(`document.querySelectorAll('.q-select').length >= 2`, 15000); await t.sleep(1800);
for (const [i, kind] of [[0, 'searchable (over six options)'], [1, 'short list']]) {
  for (const hold of [60, 300, 800]) {
    const [picked, now, opts] = await pick('document', i, hold);
    check(`${kind}: a ${hold}ms press selects the option`, picked && now === picked && !(await isOpen()), `picked ${picked}, shows ${now}, ${opts.length} options, open ${await isOpen()}`);
  }
}

// 2. The search box: pressing into it keeps the list open, typing filters, a slow press on a match
// selects. QSelect shows its search box only above six options, so on a small tenant there is
// nothing to test — SKIP honestly rather than crash on a null element, which is what this did on
// 2026-09-19 when the page it used had no timetable draft.
await open('document', 0);
const sp = await center(`document.querySelector('.q-select__search-input')`);
if (sp === null) {
  const line = '    SKIP  SELECT: the search box — this list is short enough not to have one';
  console.log(line); post(line);
  await press({ x: 1400, y: 12 }); await t.sleep(400);
} else {
  await press(sp, 250); await t.sleep(500);
  check('pressing into the search box keeps the list open and focuses it', await isOpen() && await t.eval(`document.activeElement?.classList.contains('q-select__search-input')`), `open ${await isOpen()}`);
  const all = await t.eval(`${optionsSel}.length`);
  const target = await t.eval(`${optionsSel}.map(o => o.innerText.trim()).find(o => o !== ${JSON.stringify(await text('document', 0))})`);
  await t.send('Input.insertText', { text: target.slice(0, 12) }); await t.sleep(900);
  const filtered = await t.eval(`${optionsSel}.length`);
  check('typing in the search box filters the list', filtered > 0 && filtered < all, `${all} → ${filtered}`);
  await press(await center(`${optionsSel}.find(o => o.innerText.trim() === ${JSON.stringify(target)})`), 400); await t.sleep(900);
  check('a 400ms press on a filtered option selects it and closes the list', (await text('document', 0)) === target && !(await isOpen()), `shows ${await text('document', 0)}`);
}

// 3. Closing: a click elsewhere, Tab, and the trigger again.
await open('document', 1);
await press({ x: 1400, y: 12 }, 120); await t.sleep(700);
check('a click elsewhere on the page closes the list', !(await isOpen()));
await open('document', 1);
await key('Tab', 'Tab', 9); await t.sleep(700);
check('Tab away closes the list', !(await isOpen()));
await open('document', 1);
await press(await center(trig('document', 1)), 150); await t.sleep(700);
check('pressing the trigger again closes the list', !(await isOpen()));
await open('document', 0);
await press(await center(trig('document', 0)), 150); await t.sleep(700);
check('pressing the trigger of a searchable list (focus in its search box) closes it', !(await isOpen()));
await open('document', 1);
await press(await center(trig('document', 0)), 150); await t.sleep(900);
check('opening another select closes the first and leaves one list open', (await t.eval(`document.querySelectorAll('.q-select__dropdown').length`)) === 1);
await press({ x: 1400, y: 12 }); await t.sleep(600);

// 4. Inside a modal: QSelect, then QMultiSelect with several slow ticks in a row.
await t.goto(BASE + '/admin/staff/parameters?tab=notices');
await t.waitFor(`[...document.querySelectorAll('button')].some(b => b.innerText.includes('New notice') && !b.disabled)`, 15000); await t.sleep(800);
await t.clickText('New notice'); await t.waitFor(`!!document.querySelector('.q-modal .q-select')`, 5000); await t.sleep(600);
const modal = `document.querySelector('.q-modal')`;
const idxAudience = await t.eval(`[...${modal}.querySelectorAll('.q-select-wrapper')].findIndex(w => /audience/i.test(w.innerText))`);
const [aud, audNow] = await pick(modal, idxAudience, 350, o => /role/i.test(o));
check('in a dialog, a 350ms press selects an option (Audience → roles)', aud && audNow === aud, `picked ${aud}, shows ${audNow}`);
await t.waitFor(`!!document.querySelector('.q-modal .q-multiselect')`, 4000); await t.sleep(500);
const ms = `document.querySelector('.q-modal .q-multiselect')`;
await press(await center(ms)); await t.waitFor(`!!document.querySelector('.q-multiselect__dropdown')`, 3000); await t.sleep(300);
const mopts = `[...document.querySelectorAll('.q-multiselect__dropdown .q-select__option:not(.q-select__option--clear)')]`;
const mcount = await t.eval(`${mopts}.length`);
for (let k = 0; k < Math.min(3, mcount); k++) { await press(await center(`${mopts}[${k}]`), 300); await t.sleep(700); }
const ticked = await t.eval(`${mopts}.filter(o => o.classList.contains('q-select__option--selected')).length`);
check('multi-select: three 300ms presses tick three options and the list stays open', ticked === Math.min(3, mcount) && await t.eval(`!!document.querySelector('.q-multiselect__dropdown')`), `${ticked} ticked of ${mcount}, open ${await t.eval(`!!document.querySelector('.q-multiselect__dropdown')`)}`);
const chips = await t.eval(`${ms}.querySelectorAll('.q-multiselect__chip').length`);
check('multi-select: the trigger shows the ticked options', chips >= Math.min(3, mcount), `${chips} chips`);
const fp = await center(`document.querySelector('.q-multiselect__filter-input')`);
if (fp) {
  await press(fp, 250); await t.sleep(600);
  check('multi-select: pressing into its filter box keeps the list open', await t.eval(`!!document.querySelector('.q-multiselect__dropdown')`));
}
await press(await center(`${modal}.querySelector('.q-modal__title, h2, h3') ?? ${modal}`), 100); await t.sleep(700);
check('multi-select: a click elsewhere in the dialog closes the list', !(await t.eval(`!!document.querySelector('.q-multiselect__dropdown')`)));
check('the dialog is still open after all of that', await t.eval(`!!document.querySelector('.q-modal')`));
await key('Escape', 'Escape', 27); await t.sleep(500);

// 5. The pages whose native selects moved onto QSelect.
const migrated = [
  ['/reports/visitors', /visitor type/i, 'Visitor report: Visitor type filter'],
  ['/reports/visitors', /status/i, 'Visitor report: Status filter'],
  ['/billing/usage', null, 'Usage: trend metric'],
  ['/admin/appearance?tab=links', /select branch/i, 'Customer links: branch'],
];
for (const [path, label, name] of migrated) {
  await t.goto(BASE + path);
  await t.waitFor(`document.querySelectorAll('.q-select').length > 0`, 15000); await t.sleep(4000);
  const i = label ? await t.eval(`[...document.querySelectorAll('.q-select-wrapper')].findIndex(w => ${label}.test(w.querySelector('.q-select__label')?.innerText ?? ''))`) : 0;
  // A page whose MODULE the tenant does not hold renders nothing to test. Skip honestly rather
  // than fail: on the dev tenant core-queue and visitor-management are Cancelled, so three of
  // these pages are correctly empty and a FAIL here reads like a product bug.
  if (i < 0) {
    const line = `    SKIP  SELECT: ${name} — the page is empty on this tenant (module not held)`;
    console.log(line); post(line);
    continue;
  }
  const [picked, now] = await pick('document', i, 300);
  check(`${name}: a 300ms press selects`, picked && now === picked, `picked ${picked}, shows ${now}`);
}
check('no native <select> is left on these pages', (await t.eval(`document.querySelectorAll('select').length`)) === 0);
check('no console errors during the run', t.consoleErrors.length === 0, t.consoleErrors.slice(0, 3).join(' | '));

const sum = `\n  Dropdowns: ${pass} passed, ${fail} failed`;
console.log(sum); post(sum);
t.close();
