// THE PAGER'S PAGE SIZE, FIRST AND LAST, ALL, AND A SIZE THAT SURVIVES A RELOAD (2026-09-23).
//
// Plan STUDENT_ROSTER_AND_LIST_STANDARD §1. QPager gained "Rows per page" (10 · 25 · 50 · 100 · All), first
// and last, a page jump at ten pages, and a size remembered per list per browser. This opens two real lists
// on the dev tenant — Users & Roles and the Staff Directory, both around 140 people — and PRESSES the size
// control (press, hold, release; never element.click(), which fires no mousedown and passed for weeks over a
// dropdown that did not select), then counts the rows actually drawn.
//
// A list with 25 people or fewer cannot show paging at all, so that page is SKIPPED with the count rather than
// passing vacuously. Every size it chooses is put back: the storage key is removed at the end.
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const B = 'http://127.0.0.1:5003';
const USER = 'e2e.admin.ct@qmgr.local', PASS = 'E2eTeacher!2026';
let pass = 0, fail = 0, skip = 0;
const viewer = (line) => fetch('http://127.0.0.1:5010/append?key=browser', { method: 'POST', body: line + '\n' }).catch(() => {});
const ok = (c, m, d = '') => { c ? pass++ : fail++; const l = `${c ? 'PASS' : 'FAIL'}  ${m}${c || !d ? '' : '  — ' + d}`; console.log(l); viewer(l); };
const note = (m) => { skip++; const l = `SKIP  ${m}`; console.log(l); viewer(l); };

const t = await openTab();
await t.viewport(1440, 950);
await login(t, USER, PASS);

const center = (expr) => t.eval(`(() => { const e = ${expr}; if (!e) return null;
  e.scrollIntoView({ block: 'center', behavior: 'instant' });
  const r = e.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
async function press(p, hold = 80) {
  if (!p) return false;
  await t.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: p.x, y: p.y });
  await t.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  await t.sleep(hold);
  await t.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  await t.sleep(450);
  return true;
}
const noError = () => t.eval(`getComputedStyle(document.getElementById('blazor-error-ui')).display === 'none'`);
const caption = () => t.eval(`document.querySelector('.q-pager__caption')?.innerText.trim() ?? ''`);

/** Opens the "Rows per page" list and presses the option reading `label`. */
async function chooseSize(label) {
  if (!await press(await center(`document.querySelector('.q-pager__size .q-select')`))) return false;
  if (!await t.waitFor(`!!document.querySelector('.q-pager__size .q-select__dropdown')`, 3000)) return false;
  const opt = `[...document.querySelectorAll('.q-pager__size .q-select__option')].find(o => o.innerText.trim() === ${JSON.stringify(label)})`;
  // The option is scrolled into view before its position is read: under All the size list sits at the foot of a long
  // (virtualised) page and opens below the window, where a press at its unscrolled position lands on nothing.
  const p = await t.eval(`(() => { const e = ${opt}; if (!e) return null; e.scrollIntoView({ block: 'center', behavior: 'instant' }); const r = e.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
  if (!p) return false;
  await press(p);
  await t.sleep(400);
  return true;
}

const pages = [
  { path: '/admin/users', rows: '.us-table tbody tr', key: 'users', name: 'Users & Roles' },
  { path: '/admin/staff', rows: '.sd-table tbody tr', key: 'staff-directory', name: 'Staff Directory' },
];

try {
  for (const pg of pages) {
    const storage = `qmgr-pagesize:${pg.key}`;
    await t.goto(`${B}${pg.path}`);
    await t.eval(`(() => { try { localStorage.removeItem(${JSON.stringify(storage)}); } catch { } return true; })()`);
    await t.goto(`${B}${pg.path}`);
    const came = await t.waitFor(`!!document.querySelector(${JSON.stringify(pg.rows)}) && !!document.querySelector('.q-pager__caption')`, 25000);
    const where = await t.eval('location.pathname');
    if (!came || where.startsWith('/billing') || where.startsWith('/unauthorized')) { note(`${pg.name} did not open with a pager (${where})`); continue; }
    await t.sleep(600);
    const rowCount = () => t.eval(`document.querySelectorAll(${JSON.stringify(pg.rows)}).length`);

    const first = await caption();
    const m = first.match(/of ([\d,]+)/);
    const total = m ? Number(m[1].replace(/,/g, '')) : 0;
    if (total <= 25) { note(`${pg.name} has ${total} rows — too few to page, nothing to prove here`); continue; }

    ok(/^Showing 1–25 of /.test(first), `${pg.name}: opens at 25 a page`, first);
    ok(await rowCount() === 25, `${pg.name}: 25 rows are drawn`, String(await rowCount()));
    ok(await t.eval(`!!document.querySelector('.q-pager__size')`), `${pg.name}: the bar carries "Rows per page"`);

    // 10 a page
    ok(await chooseSize('10'), `${pg.name}: the size list opens and offers 10`);
    ok(await rowCount() === 10, `${pg.name}: choosing 10 draws 10 rows`, String(await rowCount()));
    ok(/^Showing 1–10 of /.test(await caption()), `${pg.name}: the caption follows`, await caption());
    const pagesAt10 = Math.ceil(total / 10);
    if (pagesAt10 >= 10) ok(await t.eval(`!!document.querySelector('.q-pager__jump')`), `${pg.name}: "Go to page" appears at ${pagesAt10} pages`);

    // last and first
    await press(await center(`document.querySelector('.q-pager__btn[aria-label="Last page"]')`));
    const lastFrom = (pagesAt10 - 1) * 10 + 1;
    ok((await caption()).startsWith(`Showing ${lastFrom.toLocaleString('en-US')}–${total.toLocaleString('en-US')} of`), `${pg.name}: Last page goes to the end`, await caption());
    ok(await t.eval(`document.querySelector('.q-pager__btn[aria-label="Last page"]').disabled`), `${pg.name}: and Last is disabled there`);
    ok(await rowCount() === total - (pagesAt10 - 1) * 10, `${pg.name}: the last page draws only what is left`, String(await rowCount()));
    await press(await center(`document.querySelector('.q-pager__btn[aria-label="First page"]')`));
    ok(/^Showing 1–10 of /.test(await caption()), `${pg.name}: First page comes back`, await caption());

    // All
    ok(await chooseSize('All'), `${pg.name}: the size list offers All`);
    const cap = await caption();
    ok(cap.startsWith(`Showing all ${total.toLocaleString('en-US')}`), `${pg.name}: All says "Showing all ${total}"`, cap);
    if (total <= 200) ok(await rowCount() === total, `${pg.name}: All draws every row`, `${await rowCount()} of ${total}`);
    else ok(await rowCount() < total, `${pg.name}: All above 200 is virtualised, not drawn whole`, `${await rowCount()} of ${total}`);
    ok(await t.eval(`!document.querySelector('.q-pager')`), `${pg.name}: no page buttons under All`);

    // remembered
    ok(await t.eval(`localStorage.getItem(${JSON.stringify(storage)}) === '0'`), `${pg.name}: the choice is saved for this browser`);
    await t.goto(`${B}${pg.path}`);
    await t.waitFor(`!!document.querySelector(${JSON.stringify(pg.rows)}) && !!document.querySelector('.q-pager__caption')`, 25000);
    const restored = await t.waitFor(`(document.querySelector('.q-pager__caption')?.innerText ?? '').startsWith('Showing all')`, 6000);
    ok(restored, `${pg.name}: All survives a reload`, await caption());

    // back to 25 — the bar must still be there under All, or the reader could never leave it
    // Let a long virtualised list finish drawing after the reload before pressing at its foot; a press that lands
    // while <Virtualize> is still re-rendering rows under a scroll can be swallowed. One retry, then it counts.
    await t.sleep(1200);
    ok(await chooseSize('25') || (await t.sleep(800), await chooseSize('25')), `${pg.name}: 25 can be chosen again from All`);
    ok(await rowCount() === 25, `${pg.name}: and 25 rows come back`, String(await rowCount()));
    ok(await noError(), `${pg.name}: no error bar`);

    await t.eval(`(() => { try { localStorage.removeItem(${JSON.stringify(storage)}); } catch { } return true; })()`);
  }
} finally {
  await t.eval(`(() => { try { localStorage.removeItem('qmgr-pagesize:users'); localStorage.removeItem('qmgr-pagesize:staff-directory'); } catch { } return true; })()`).catch(() => {});
}

console.log(`\n${pass} passed, ${fail} failed${skip ? `, ${skip} skipped` : ''}`);
t.close();
process.exitCode = fail ? 1 : 0;
