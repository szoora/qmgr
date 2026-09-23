// The 2026-09-19 compaction pass and the staff record it surfaced.
//
// Two subjects, one suite, because both came out of the same session and both are invisible to a
// curl suite by construction — one is a measurement of the rendered page, the other is a dialog.
//
//   1  the page band is one row, not two, and its title has ONE home in the cascade
//   2  QInfo carries the explanation that used to sit on the page for ever
//   3  a timeline renders a page at a time, not its whole history
//   4  the dashboard's figures carry a period, and its LIVE panels say they are live
//   5  the staff record — twelve fields that only the bulk import could write until today
//   6  adding staff no longer navigates out of the module
//
// Run with a HEADED Chrome on 9333 so it can be watched.
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = 'http://127.0.0.1:5003';
let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const skipped = (name, why) => { skip++; const l = `    SKIP  ${name} — ${why}`; console.log(l); post(l); };

const t = await openTab();
await t.viewport(1500, 950);
await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');

const settle = async (route, ms = 2500) => {
  await t.goto(BASE + route);
  await t.waitFor(`!!document.querySelector('.qm-main')`, 20000);
  await t.sleep(ms);
};

// ---------------------------------------------------------------- 1. the band
post('\n== The page band is one row ==');

for (const route of ['/admin/branches', '/admin/students/roster', '/admin/staff?tab=class-teachers', '/content/library']) {
  await settle(route);
  const m = JSON.parse(await t.eval(`(() => {
    const h = document.querySelector('.qm-main .page-header');
    if (!h) return JSON.stringify({ missing: true });
    const hc = h.querySelector('.header-content, .header-left');
    const h1 = document.querySelector('.qm-main h1');
    const cs = getComputedStyle(h1);
    let winner = null;
    for (const sheet of document.styleSheets) {
      let rules; try { rules = sheet.cssRules; } catch { continue; }
      for (const r of rules) {
        if (!r.selectorText || !r.style || !r.style.fontSize) continue;
        try { if (h1.matches(r.selectorText)) winner = r.selectorText; } catch {}
      }
    }
    return JSON.stringify({
      band: Math.round(h.getBoundingClientRect().height),
      titleRow: hc ? Math.round(hc.getBoundingClientRect().height) : null,
      size: cs.fontSize, winner,
      btn: (() => { const b = document.querySelector('.qm-main .q-btn:not(.q-btn--sm):not(.q-btn--lg)'); return b ? Math.round(b.getBoundingClientRect().height) : null; })()
    });
  })()`));
  if (m.missing) { skipped(`${route}: band`, 'no page header on this route'); continue; }
  check(`${route}: the band is at most 60px`, m.band <= 60, `${m.band}px`);
  // One ROW: the title and its subtitle share a line, so the block is one line box tall.
  check(`${route}: title and subtitle share one row`, m.titleRow === null || m.titleRow <= 34, `${m.titleRow}px`);
  check(`${route}: the title is 20px and .qm-main h1 wins`, m.size === '20px' && (m.winner || '').startsWith('.qm-main h1'), `${m.size} via ${m.winner}`);
  check(`${route}: a default button is 32px`, m.btn === null || m.btn === 32, `${m.btn}px`);
}

// ---------------------------------------------------------------- 2. QInfo
post('\n== QInfo carries the explanation ==');

await settle('/admin/staff?tab=class-teachers');
const infoCount = Number(await t.eval(`document.querySelectorAll('.qm-main .q-info__btn').length`));
check('the page offers a QInfo beside its title', infoCount >= 1, `${infoCount} found`);

if (infoCount >= 1) {
  await t.eval(`(() => { const b = document.querySelector('.qm-main .q-info__btn'); b.focus(); b.click(); })()`);
  await t.sleep(600);
  const openPop = await t.eval(`(() => { const p = document.querySelector('.q-info__pop'); return p ? p.innerText.trim().length : 0; })()`);
  check('pressing it opens a popover with the explanation', Number(openPop) > 20, `${openPop} characters`);

  // Escape closes it — a popover a keyboard user cannot dismiss is worse than the tooltip it replaced.
  await t.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await t.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await t.sleep(500);
  check('Escape closes it', !(await t.eval(`!!document.querySelector('.q-info__pop')`)));
}

// ---------------------------------------------------------------- 3. timeline paging
post('\n== A timeline renders a page at a time ==');

// The portal is a four-tab hub since 2026-09-21 and a section renders only while its own
// tab is open — that lazy render IS the point of the hub. #my-timeline and #my-details both
// live under "My file", so a bare /portal reads an empty Today tab and reports nothing.
await settle('/portal?tab=file', 3500);
const tl = JSON.parse(await t.eval(`(() => {
  const items = document.querySelectorAll('#my-timeline .q-timeline__item, #my-timeline .q-timeline .q-timeline__item');
  const more = [...document.querySelectorAll('#my-timeline button')].find(b => /show older/i.test(b.innerText));
  return JSON.stringify({ shown: items.length, more: more ? more.innerText.trim() : null });
})()`));
if (tl.shown === 0) skipped('my timeline paging', 'no records on this account');
else {
  check('the timeline renders at most one page (25)', tl.shown <= 25, `${tl.shown} items`);
  if (tl.more) {
    const before = tl.shown;
    await t.eval(`[...document.querySelectorAll('#my-timeline button')].find(b => /show older/i.test(b.innerText)).click()`);
    await t.sleep(1200);
    const after = Number(await t.eval(`document.querySelectorAll('#my-timeline .q-timeline__item').length`));
    check('"Show older" reveals the next page', after > before, `${before} → ${after}`);
  } else {
    skipped('"Show older"', 'this account has fewer than one page of records');
  }
}

// ---------------------------------------------------------------- 4. the dashboard's period
post('\n== The dashboard carries a period, and says which figures are live ==');

await settle('/', 4500);   // the dashboard is the root route
const dash = JSON.parse(await t.eval(`(() => {
  const picker = document.querySelector('.qm-main .q-daterange');
  const periods = [...document.querySelectorAll('.module-section-period')].map(e => e.innerText.trim());
  const visitorPanel = [...document.querySelectorAll('.module-section-head h2')].some(h => /visitor/i.test(h.innerText));
  return JSON.stringify({ picker: !!picker, periods, visitorPanel });
})()`));
check('the dashboard has a date-range control', dash.picker, JSON.stringify(dash));
check('a period panel states the range it covers', dash.periods.some(p => p && !/live/i.test(p)), JSON.stringify(dash.periods));
// The LIVE label lives on the Visitor Management panel, which a tenant without that module does
// not see at all. Skip honestly rather than report the module gate as a missing label.
if (dash.visitorPanel) check('the live panel is labelled Live, not given a range', dash.periods.some(p => /live/i.test(p)), JSON.stringify(dash.periods));
else skipped('the Live label', 'this tenant does not hold Visitor Management');

// ---------------------------------------------------------------- 5. the staff record
post('\n== The staff record: readable and correctable at last ==');

await settle('/admin/staff', 3500);
const hasRecordBtn = await t.eval(`[...document.querySelectorAll('.qm-main button')].some(b => b.innerText.trim() === 'Record')`);
if (!hasRecordBtn) skipped('the staff record dialog', 'no staff rows on this branch');
else {
  await t.eval(`[...document.querySelectorAll('.qm-main button')].find(b => b.innerText.trim() === 'Record').click()`);
  await t.waitFor(`!!document.querySelector('.q-modal')`, 8000);
  await t.sleep(2200);
  const dlg = JSON.parse(await t.eval(`(() => {
    const m = document.querySelector('.q-modal');
    if (!m) return JSON.stringify({ open: false });
    const labels = [...m.querySelectorAll('.q-input__label, .q-select__label, .q-datepicker__label, label')].map(e => e.innerText.trim()).filter(Boolean);
    const heads = [...m.querySelectorAll('.sp-head')].map(e => e.innerText.trim().split('\\n')[0]);
    return JSON.stringify({ open: true, labels, heads, save: [...m.querySelectorAll('button')].some(b => /save record/i.test(b.innerText)) });
  })()`));
  check('the staff record dialog opens', dlg.open);
  // The twelve fields that only the bulk import could write until today.
  for (const field of ['Qualification', 'Registration number', 'Staff number', 'Terms', 'Appointed', 'National ID', 'Emergency contact']) {
    check(`it carries "${field}"`, (dlg.labels || []).some(l => l.toLowerCase().includes(field.toLowerCase())), JSON.stringify(dlg.labels));
  }
  check('contact and employment are separate sections', (dlg.heads || []).length >= 2, JSON.stringify(dlg.heads));
  check('it can be saved', dlg.save === true, String(dlg.save));
  await t.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await t.sleep(600);
}

// ---------------------------------------------------------------- 6. adding staff in place
post('\n== Adding staff does not leave the module ==');

await settle('/admin/staff', 3000);
const addBtn = await t.eval(`[...document.querySelectorAll('.qm-main button')].some(b => /add staff/i.test(b.innerText))`);
if (!addBtn) skipped('Add staff', 'the caller cannot create users');
else {
  await t.eval(`[...document.querySelectorAll('.qm-main button')].find(b => /add staff/i.test(b.innerText)).click()`);
  await t.sleep(2200);
  const after = JSON.parse(await t.eval(`JSON.stringify({ url: location.pathname, modal: !!document.querySelector('.q-modal') })`));
  check('it opens a dialog instead of navigating to Users & Roles', after.modal && after.url === '/admin/staff', JSON.stringify(after));
  const fields = JSON.parse(await t.eval(`JSON.stringify([...document.querySelectorAll('.q-modal .q-input__label, .q-modal .q-select__label')].map(e => e.innerText.trim()))`));
  for (const f of ['First name', 'Last name', 'Email', 'Role']) {
    check(`the dialog asks for "${f}"`, fields.some(x => x.toLowerCase().includes(f.toLowerCase())), JSON.stringify(fields));
  }
}

// ---------------------------------------------------------------- 7. my own details
post('\n== A person can maintain their own contact detail ==');

// The portal is a four-tab hub since 2026-09-21 and a section renders only while its own
// tab is open — that lazy render IS the point of the hub. #my-timeline and #my-details both
// live under "My file", so a bare /portal reads an empty Today tab and reports nothing.
await settle('/portal?tab=file', 3500);
const mine = JSON.parse(await t.eval(`(() => {
  const card = document.querySelector('#my-details');
  if (!card) return JSON.stringify({ card: false });
  const btn = [...card.querySelectorAll('button')].some(b => /update my contact/i.test(b.innerText));
  const locked = !!card.querySelector('.md-list--locked');
  const labels = [...card.querySelectorAll('dt')].map(e => e.innerText.trim());
  return JSON.stringify({ card: true, btn, locked, labels });
})()`));
check('the portal shows my own details', mine.card === true, JSON.stringify(mine));
if (mine.card) {
  check('I can edit my contact detail', mine.btn === true);
  check('the employment half is shown read-only', mine.locked === true);
  check('it lists what the school holds', (mine.labels || []).some(l => /qualification|registration|employee number/i.test(l)), JSON.stringify(mine.labels));
}

check('no console errors across the run', t.consoleErrors.length === 0, t.consoleErrors.slice(0, 3).join(' | '));

console.log(`\n  ${pass} passed, ${fail} failed, ${skip} skipped`);
post(`\n  ${pass} passed, ${fail} failed, ${skip} skipped`);
await t.close();
process.exit(fail ? 1 : 0);
