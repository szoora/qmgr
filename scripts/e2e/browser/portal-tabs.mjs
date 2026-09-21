// MY WORKSPACE: all four tabs, opened by deep link and by press, for two different people.
//
// /portal became a hub on 2026-09-21 and nothing opened its tabs in a browser. The teaching tab then
// shipped carrying <QModal Open="@askOpen"> — QModal takes Visible — which is a RUNTIME failure:
// Blazor throws "does not have a property matching the name 'Open'" the first time the component
// renders, the circuit is torn down, and the whole page becomes "An unhandled error has occurred".
// The build was clean, the API suite (section 21) passed, and the tab was dead for everybody.
//
// scripts/e2e/component-param-check.mjs now catches that exact shape statically. This is the layer
// above it: a tab can be killed by any exception on its first render, not only by a bad parameter
// name, and the only way to see that is to open it.
//
// Two people, because the sections differ: a teacher has a timetable and lessons, an administrator
// has neither, and a tab that renders for one can throw for the other.
//
// Run: node scripts/e2e/browser/portal-tabs.mjs   (headless Chrome on 9333; see CLAUDE.md)
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';

const BASE = process.env.WEB ?? 'http://127.0.0.1:5003';
const API = process.env.API ?? 'http://127.0.0.1:5001';
const ADMIN = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const ADMIN_PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';
const TEACHER = process.env.E2E_TEACHER_USER ?? 'e2e.sp.math1@qmgr.local';
const TEACHER_PASSWORDS = process.env.E2E_TEACHER_PASS
  ? [process.env.E2E_TEACHER_PASS]
  : ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];

const TABS = [
  { key: 'today', label: 'Today' },
  { key: 'teaching', label: 'My teaching' },
  { key: 'performance', label: 'My performance' },
  { key: 'file', label: 'My file' },
];

let pass = 0, fail = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};

const findPassword = async (email, passwords) => {
  for (const password of [].concat(passwords)) {
    const r = await fetch(`${API}/api/v1/auth/login`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }),
    });
    if (r.ok) return password;
  }
  return null;
};

const teacherPass = await findPassword(TEACHER, TEACHER_PASSWORDS);
if (!teacherPass) { console.error(`could not sign in as ${TEACHER}; set E2E_TEACHER_USER / E2E_TEACHER_PASS`); process.exit(1); }
if (!await findPassword(ADMIN, ADMIN_PASS)) { console.error(`could not sign in as ${ADMIN}`); process.exit(1); }

const t = await openTab();
const strip = '[aria-label="My Workspace"], .qm-main .q-tabs';
// #blazor-error-ui is on every Blazor page and hidden by a STYLESHEET, so it is read computed
// (CLAUDE.md) — matching on the inline style reports an error bar on every page.
const crashed = () => t.eval(`(() => { const e = document.querySelector('#blazor-error-ui');
  return !!e && getComputedStyle(e).display !== 'none'; })()`);
const activeTab = () => t.eval(`(() => { const s = document.querySelector('${strip}');
  const b = s && s.querySelector('[role="tab"][aria-selected="true"]'); return b ? b.innerText.trim() : null; })()`);
const sectionText = () => t.eval(`(() => { const m = document.querySelector('.qm-main');
  return m ? m.innerText.replace(/\\s+/g, ' ').trim().length : 0; })()`);
const pressTab = async (label) => {
  const hit = await t.eval(`(() => { const s = document.querySelector('${strip}'); if (!s) return false;
    const b = [...s.querySelectorAll('[role="tab"]')].find(x => x.innerText.trim() === ${JSON.stringify(label)});
    if (!b) return false; b.click(); return true; })()`);
  await t.sleep(1600);
  return hit;
};

const walk = async (who, password, note) => {
  await login(t, who, password);
  for (const tab of TABS) {
    await t.goto(`${BASE}/portal${tab.key === 'today' ? '' : `?tab=${tab.key}`}`);
    await t.waitFor(`!!document.querySelector('${strip}')`, 30000);
    await t.sleep(1800);
    const [bar, active, len] = [await crashed(), await activeTab(), await sectionText()];
    check(`${note}: ?tab=${tab.key} survives its first render`, !bar,
      'the circuit died — read the console for the exception');
    check(`${note}: ?tab=${tab.key} opens "${tab.label}" with something on it`,
      active === tab.label && len > 200, `active=${JSON.stringify(active)} text=${len}`);
  }
  // Pressing through them in one circuit is a different path from a fresh load: a same-route ?tab=
  // navigation does not re-run OnInitializedAsync, so the hub follows the query itself.
  for (const tab of TABS.slice(1)) {
    const pressed = await pressTab(tab.label);
    check(`${note}: pressing "${tab.label}" moves to it and stays alive`,
      pressed && await activeTab() === tab.label && !(await crashed()),
      `pressed=${pressed} active=${await activeTab()}`);
  }
};

try {
  await walk(TEACHER, teacherPass, 'a teacher');
  await walk(ADMIN, ADMIN_PASS, 'an administrator');
} catch (e) {
  check('the suite ran to the end', false, e.message);
} finally {
  const errs = t.consoleErrors.filter(e => /InvalidOperationException|NullReference|unhandled exception/i.test(e ?? ''));
  check('no unhandled exception reached the browser console', errs.length === 0, errs.slice(0, 2).join(' | ').slice(0, 300));
  t.close();
}

const line = `  portal-tabs: ${pass} passed, ${fail} failed`;
console.log(line); post(line);
process.exitCode = fail ? 1 : 0;
