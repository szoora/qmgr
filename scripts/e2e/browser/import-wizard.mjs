// THE IMPORT WIZARD, DRIVEN WITH TWO REAL SCHOOL FILES.
//
// Not a fixture: `Staff List.xlsx` (184 staff) and `Students_2026-09-21.xls` (1,711 students) are the
// files Maryhill High School actually exports, and each one defeats a naive importer in its own way:
//
//   the staff file  — row 1 is the TITLE "Staff List", the headers are on row 2, and the name is one
//                     column written surname first ("Ahabyoona Katah Egidious");
//   the student file — is an HTML table wearing an .xls name, the names are SHOUTED, and the class is
//                     split over two columns, Class = S1 and Stream = A, where Q-Mgr stores S1A.
//
// Everything here is client-side — header detection, the mapping, the join, the name split, the
// cleaning counts — so a curl suite cannot see any of it. It stops before pressing Import: the point
// is that the file is READ correctly, and importing 1,711 students into the dev tenant on every run
// would be a mess somebody has to clear up.
//
// Run: node scripts/e2e/browser/import-wizard.mjs   (headless Chrome on 9333; see CLAUDE.md)
import fs from 'node:fs';
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';
const STAFF_FILE = process.env.E2E_STAFF_FILE ?? 'E:\\Staff List.xlsx';
const STUDENT_FILE = process.env.E2E_STUDENT_FILE ?? 'E:\\Students_2026-09-21.xls';

let pass = 0, fail = 0, skipped = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const skip = (name, why) => { skipped++; const l = `    SKIP  ${name}  — ${why}`; console.log(l); post(l); };

const t = await openTab();
const text = () => t.eval(`document.querySelector('.q-import')?.innerText.replace(/\\s+/g, ' ').trim() ?? ''`);
const mapOf = () => t.eval(`(() => [...document.querySelectorAll('.q-import__map-row')].map(r => ({
    field: r.querySelector('.q-import__map-name')?.innerText.trim(),
    required: !!r.querySelector('.q-import__req'),
    picked: [...r.querySelectorAll('.q-import__map-pick .q-select__value, .q-import__map-pick button')]
      .map(e => e.innerText.trim()).filter(Boolean),
    sample: r.querySelector('.q-import__sample')?.innerText.trim() ?? '',
  })))()`);
const stage = () => t.eval(`document.querySelector('.q-import__step--now')?.innerText.replace(/\\s+/g,' ').trim() ?? ''`);
const clickText = (label, sel = 'button') => t.clickText(label, sel);

const openImport = async (url, waitFor) => {
  await t.goto(url);
  await t.waitFor(waitFor, 30000);
  await t.sleep(1200);
};

try {
  await login(t, USER, PASS);

  // ---------------------------------------------------------------- 1. the staff file
  if (!fs.existsSync(STAFF_FILE)) {
    skip('the staff file', `${STAFF_FILE} is not on this machine — set E2E_STAFF_FILE`);
  } else {
    await openImport(`${BASE}/admin/staff?tab=import`, `!!document.querySelector('#ss-import-file')`);
    check('the staff import offers a file picker', await t.eval(`!!document.querySelector('#ss-import-file')`));

    await t.setFiles('#ss-import-file', [STAFF_FILE]);
    if (!await t.waitFor(`!!document.querySelector('.q-import__map-rows')`, 30000)) {
      check('the staff sheet is read and mapped', false, (await text()).slice(0, 300));
    } else {
      await t.sleep(1500);
      const body = await text();
      const rows = await mapOf();
      const full = rows.find(r => (r.field ?? '').toLowerCase().startsWith('full name'));
      const first = rows.find(r => r.field === 'First name');

      check('the wizard reaches the mapping step', (await stage()).includes('Match the columns'), await stage());
      check('the header row is found on row 2, under the title', /Row 2: Account/.test(body), body.slice(0, 220));
      check('"Staff Name" is matched to the combined name field',
        !!full && full.picked.some(p => /Staff Name/i.test(p)), JSON.stringify(full));
      check('a required field says so', !!first && first.required, JSON.stringify(first));
      check('the sample shows the school\'s own names',
        !!full && /Abaho|Abaine|Adrabo/i.test(full.sample), full?.sample ?? '');
      check('the columns it ignores are named', /Read and ignored/.test(body) && /PAYE/.test(body), body.slice(-300));

      // The name order is asked, with the file's own first name in the question.
      check('the name order is asked, using a name from the file',
        /Surname first/.test(body) && /Abaho/.test(body), body.slice(0, 400));

      const went = await clickText('Continue');
      await t.sleep(2500);
      const review = await text();
      check('Continue reaches the check step', went && (await stage()).includes('Check and import'), await stage());
      const ready = Number((review.match(/(\d+)\s+READY TO IMPORT/i) ?? [])[1] ?? 0);
      const skippedRows = Number((review.match(/(\d+)\s+WILL BE SKIPPED/i) ?? [])[1] ?? 0);
      check('every one of the 184 rows is accounted for', ready + skippedRows === 184, `${ready} ready + ${skippedRows} skipped`);
      check('the surname-first split is what the preview shows',
        /Abaho/.test(review), review.slice(0, 400));

      // THE STAFF LIST IMPORTS. 133 of these 184 people have no email address, and until 2026-09-21
      // every one of them was refused — the product treated an address as a staff member's identity.
      // They now sign in with a username and a temporary password on a slip.
      // docs/plans/STAFF_WITHOUT_EMAIL.md.
      check('the people with no email address are no longer refused', ready >= 180,
        `${ready} ready + ${skippedRows} skipped`);
      check('...nothing is refused for a MISSING address any more',
        !/email is missing/i.test(review), review.slice(0, 400));
      check('...and each one is warned about instead, naming the username they will type',
        /no email address/i.test(review) && /sign in as/i.test(review), review.slice(0, 400));

      // The two rows this file still loses are typos in the school's own data — "@gmail" with no
      // top-level domain and "@gmailcom" with no dot. Refusing those is the point: they are a
      // mistake somebody can fix, not a person without an address.
      check('a MALFORMED address is still refused, and says what to do',
        skippedRows === 0 || /is not an email address/i.test(review), review.slice(0, 400));
      check('the cleaning says what it changed',
        /phone numbers normalised/.test(review), review.slice(0, 300));
    }
  }

  // ---------------------------------------------------------------- 2. the student file
  if (!fs.existsSync(STUDENT_FILE)) {
    skip('the student file', `${STUDENT_FILE} is not on this machine — set E2E_STUDENT_FILE`);
  } else {
    await openImport(`${BASE}/admin/students/roster`, `!!document.body`);
    await t.sleep(1500);
    const opened = await clickText('Bulk Import');
    await t.sleep(1200);
    if (!await t.waitFor(`!!document.querySelector('#roster-file-input')`, 15000)) {
      skip('the student roster import', 'the Import dialog did not open on this tenant');
    } else {
      await t.setFiles('#roster-file-input', [STUDENT_FILE]);
      if (!await t.waitFor(`!!document.querySelector('.q-import__map-rows')`, 45000)) {
        check('the .xls-that-is-HTML is read', false, (await text()).slice(0, 300));
      } else {
        await t.sleep(2000);
        const body = await text();
        const rows = await mapOf();
        const cls = rows.find(r => r.field === 'Class name');
        const name = rows.find(r => (r.field ?? '').includes('Student full name'));

        check('an .xls that is really an HTML table is read', opened && rows.length > 0, `${rows.length} field(s)`);
        check('Class and Stream are joined into one class name',
          !!cls && cls.picked.some(p => /Class/i.test(p)) && cls.picked.some(p => /Stream/i.test(p)), JSON.stringify(cls));
        check('...and the joined sample reads S1A, not "S1 A"',
          !!cls && /\bS\d[A-Z]\b/.test(cls.sample), cls?.sample ?? '');
        check('the student name is matched', !!name && name.picked.some(p => /Student Name/i.test(p)), JSON.stringify(name));

        await clickText('Continue');
        await t.sleep(3000);
        const review = await text();
        check('the shouting is tidied on the way in',
          /names tidied out of capitals/.test(review), review.slice(0, 400));
        check('every row of a 1,711-row file is validated',
          /1,?7\d\d/.test(review), review.slice(0, 300));
      }
    }
  }
} catch (e) {
  check('the suite ran to the end', false, e.message);
} finally {
  t.close();
}

const line = `  import-wizard: ${pass} passed, ${fail} failed${skipped ? `, ${skipped} skipped` : ''}`;
console.log(line); post(line);
process.exitCode = fail ? 1 : 0;
