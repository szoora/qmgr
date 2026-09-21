// The payment screens in a real browser (2026-09-19) — what payments-e2e.mjs cannot see: which ways to
// pay a dialog OFFERS, the required markers, the inline field errors, the "approve on your phone" panel
// and whether it closes itself when the gateway's webhook lands.
//
//   node scripts/e2e/browser/payments-ui.mjs      (API on 5001, Web on 5003, Chrome on 9333)
//
// It hosts the local gateway stub (../sacc-gateway-stub.mjs), points the platform at it through the API,
// and switches it back off at the end. A module it buys is removed again; the renewal number is put back.
import { openTab } from './cdp.mjs';
import { login } from './login.mjs';
import { startStub } from '../sacc-gateway-stub.mjs';

const BASE = 'http://127.0.0.1:5003';
const API = process.env.API || 'http://127.0.0.1:5001';
const MASK = '••••••••';
const STUB_KEY = 'stub-key-for-local-ui-' + Date.now().toString(36);
let pass = 0, fail = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + String(detail).slice(0, 300)}`;
  console.log(l); post(l);
};
const hdr = (s) => { console.log(`\n== ${s} ==`); post(`\n== ${s} ==`); };

async function api(token, method, path, body) {
  const res = await fetch(API + path, { method, headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  try { return { status: res.status, json: text ? JSON.parse(text) : null }; } catch { return { status: res.status, json: null }; }
}
async function apiLogin(id, passwords) {
  for (const pw of passwords) {
    const r = await fetch(API + '/api/v1/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: id, password: pw }) });
    const j = await r.json().catch(() => null);
    if (j?.accessToken) return j.accessToken;
  }
  return null;
}

const stub = await startStub({ port: 5098, apiKey: STUB_KEY });
const settle = (referenceId, state) => fetch(stub.url + '/__stub/settle', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ referenceId, state }) }).then(r => r.json());
const SA = await apiLogin('superadmin', ['admin']);
const AD = await apiLogin('e2e.admin.ct@qmgr.local', ['E2eTeacher!2026', 'Rwenzori#Peaks-2026']);
const cleanup = { module: null, renewal: undefined, gatewayUrl: null };

const t = await openTab();
await t.viewport(1500, 950);
const main = () => t.eval(`document.querySelector('.qm-main')?.innerText ?? ''`);
const modalText = () => t.eval(`[...document.querySelectorAll('.q-modal')].filter(m => m.offsetParent !== null).map(m => m.innerText).join('\\n')`);
// Chips, tile labels and table headers are upper-cased by CSS, and innerText returns them that way,
// so every match against their text is case-insensitive.
async function press(expr) {
  await t.eval(`(${expr})?.scrollIntoView({ block: 'center', behavior: 'instant' })`);
  const p = await t.eval(`(() => { const r = (${expr})?.getBoundingClientRect(); return r ? { x: r.x + r.width / 2, y: r.y + r.height / 2 } : null; })()`);
  if (!p) return false;
  await t.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: p.x, y: p.y });
  await t.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  await t.sleep(80);
  await t.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  return true;
}
const openPayBy = async () => {
  await press(`[...document.querySelectorAll('.q-modal .q-select')].at(-1)`);
  await t.waitFor(`!!document.querySelector('.q-select__dropdown')`, 4000);
  return t.eval(`[...document.querySelectorAll('.q-select__dropdown .q-select__option:not(.q-select__option--clear)')].map(o => o.innerText.trim())`);
};
// The button reads "Save number" with no number stored and "Change number" with one.
const saveNumber = () => t.eval(`[...document.querySelectorAll('.qm-main button')].find(b => /^(Save|Change) number$/.test(b.innerText.trim()))?.click()`);
const errorBar = () => t.eval(`(() => { const e = document.getElementById('blazor-error-ui'); return !!e && getComputedStyle(e).display !== 'none'; })()`);

try {
  const before = (await api(SA, 'GET', '/api/v1/platform/payments/gateway')).json;
  cleanup.gatewayUrl = before?.baseUrl ?? null;
  await api(SA, 'PUT', '/api/v1/platform/payments/gateway', { enabled: true, baseUrl: stub.url, apiKey: STUB_KEY, cardsEnabled: false });
  const reg = (await api(SA, 'POST', '/api/v1/platform/payments/gateway/webhook', { callbackUrl: `${API}/api/v1/payments/sacc/webhook` })).json;
  check('setup: the gateway points at the stub with a webhook', reg?.registered === true, JSON.stringify(reg));
  cleanup.renewal = (await api(AD, 'GET', '/api/v1/billing/renewal-number')).json?.phoneNumber ?? null;

  await login(t, 'e2e.admin.ct@qmgr.local', 'E2eTeacher!2026');

  // ---------------------------------------------------------------------------------------------------
  hdr('1. The Payment tab: ways to pay, and the renewal number');
  await t.goto(BASE + '/billing?tab=payment');
  await t.waitFor(`/Ways to pay/.test(document.querySelector('.qm-main')?.innerText ?? '')`, 20000);
  await t.sleep(800);
  const payTab = await main();
  check('Mobile Money reads Available', /Mobile Money[\s\S]{0,200}Available/i.test(payTab), payTab.slice(0, 400));
  check('Card reads Off (not switched on)', /Card[\s\S]{0,200}\bOff\b/i.test(payTab), payTab.slice(0, 400));
  check('nothing on the tab offers Stripe', !/Add card|Stripe/.test(payTab), 'Stripe wording present');
  check('the renewal number card is there, marked required', /Renewal number/.test(payTab) && await t.eval(`!!document.querySelector('.qm-main .q-input__required')`));
  await t.setValue('.qm-main input[type=tel]', '0733 123 456');
  await t.sleep(300);
  await saveNumber();
  const refused = await t.waitFor(`/not on a network/.test(document.querySelector('.qm-main')?.innerText ?? '')`, 5000);
  check('a number on no network is refused inline', refused, (await main()).slice(-400));
  await t.setValue('.qm-main input[type=tel]', '0772 555 111');
  await t.sleep(400);
  check('a valid number is named by its network as it is typed', /MTN · 0772 555 111/.test(await main()));
  await saveNumber();
  let savedNumber = null;
  for (let i = 0; i < 20 && savedNumber !== '256772555111'; i++) { await t.sleep(500); savedNumber = (await api(AD, 'GET', '/api/v1/billing/renewal-number')).json?.phoneNumber; }
  check('saving stores it normalized', savedNumber === '256772555111', savedNumber);
  check('with a number saved, the button reads Change number and Remove number is offered',
    await t.eval(`[...document.querySelectorAll('.qm-main button')].some(b => b.innerText.trim() === 'Change number') && [...document.querySelectorAll('.qm-main button')].some(b => b.innerText.trim() === 'Remove number')`));

  // Remove, with the confirmation.
  await t.clickText('Remove number', '.qm-main button');
  await t.waitFor(`/Remove the renewal number/.test([...document.querySelectorAll('.q-modal')].map(m => m.innerText).join(' '))`, 5000);
  check('removing asks for confirmation and says what changes', /no longer be charged automatically/.test(await modalText()), (await modalText()).slice(0, 200));
  await t.clickText('Remove number', '.q-modal button');
  const removed = await t.waitFor(`![...document.querySelectorAll('.q-modal')].some(m => m.offsetParent !== null && /Remove the renewal number/.test(m.innerText))`, 8000);
  check('confirming removes it', removed && (await api(AD, 'GET', '/api/v1/billing/renewal-number')).json?.phoneNumber == null);
  check('the card stays, with Save number and no Remove', await t.eval(`[...document.querySelectorAll('.qm-main button')].some(b => b.innerText.trim() === 'Save number') && ![...document.querySelectorAll('.qm-main button')].some(b => b.innerText.trim() === 'Remove number')`));

  hdr('1b. The Overview tile');
  await t.goto(BASE + '/billing?tab=overview');
  await t.waitFor(`/Renewal number/i.test(document.querySelector('.qm-main')?.innerText ?? '')`, 20000);
  await t.sleep(600);
  check('with none set, the Overview tile says Not set', /Not set[\s\S]{0,40}Renewal number|Renewal number[\s\S]{0,60}Not set/i.test(await main()), (await main()).slice(0, 500));
  await api(AD, 'PUT', '/api/v1/billing/renewal-number', { phoneNumber: '0772 555 111' });
  await t.goto(BASE + '/billing?tab=overview');
  await t.waitFor(`/0772 555 111/.test(document.querySelector('.qm-main')?.innerText ?? '')`, 20000);
  check('with one set, the tile shows it', /0772 555 111/.test(await main()));
  const tileHref = await t.eval(`[...document.querySelectorAll('.qm-main a')].find(a => /Renewal number/i.test(a.innerText))?.getAttribute('href')`);
  check('the tile links to the Payment tab\'s renewal number', /tab=payment#renewal-number$/.test(tileHref ?? ''), tileHref);
  await t.goto(BASE + '/billing?tab=payment#renewal-number');
  check('following it opens the Payment tab with the renewal number card', await t.waitFor(`!!document.getElementById('renewal-number')`, 20000));

  // ---------------------------------------------------------------------------------------------------
  hdr('2. Adding a module: only what the platform offers, every field required');
  const catalog = (await (await fetch(API + '/api/v1/modules')).json()) ?? [];
  const mine = async () => (await api(AD, 'GET', '/api/v1/modules/mine')).json ?? [];
  const owned = await mine();
  const trial = owned.find(m => m.status === 'Trialing');
  const target = trial ? catalog.find(c => c.code === trial.moduleCode) : catalog.find(c => !owned.some(m => m.moduleCode === c.code && m.purchased));
  if (!target) throw new Error('no module to buy');
  if (!trial) cleanup.module = target.code;

  await t.goto(BASE + '/billing?tab=modules');
  await t.waitFor(`!!document.querySelector('.qm-main table.data-table')`, 20000);
  await t.sleep(800);
  const opened = await t.eval(`(() => { const row = [...document.querySelectorAll('.qm-main tr')].find(r => r.innerText.includes(${JSON.stringify(target.name)}));
    const b = row && [...row.querySelectorAll('button')].find(x => /^(Add|Pay)$/.test(x.innerText.trim())); if (!b) return false; b.click(); return true; })()`);
  check(`the ${target.name} row has an Add / Pay button`, opened);
  await t.waitFor(`/Send prompt/.test([...document.querySelectorAll('.q-modal')].map(m => m.innerText).join(' '))`, 15000);
  let dialog = await modalText();
  check('the dialog says what the asterisk means', /\* Required/.test(dialog), dialog.slice(0, 300));
  check('Pay by shows Mobile Money', /Mobile Money \(MTN, Airtel\)/.test(dialog));
  const options = await openPayBy();
  check('the only way to pay offered is Mobile Money (cards are off, Stripe is off)', options.length > 0 && options.every(o => /Mobile Money/.test(o)), JSON.stringify(options));
  await t.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await t.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await t.sleep(300);
  dialog = await modalText();
  check('the renewal number is filled in for them', await t.eval(`document.querySelector('.q-modal input[type=tel]')?.value`) === '0772 555 111');
  check('the amount due is shown before paying', /Due now[\s\S]*UGX/.test(dialog));

  await t.setValue('.q-modal input[type=tel]', '');
  await t.clickText('Send prompt', '.q-modal button');
  await t.sleep(600);
  dialog = await modalText();
  check('submitting with the number empty refuses, and says where', /Complete the fields marked in red/.test(dialog) && /Enter a mobile money number/.test(dialog), dialog.slice(0, 400));
  check('nothing reached the gateway', stub.state.collects.size === 0);

  await t.setValue('.q-modal input[type=tel]', '0772 555 111');
  await t.clickText('Send prompt', '.q-modal button');
  const waiting = await t.waitFor(`/Approve the prompt on your phone/.test([...document.querySelectorAll('.q-modal')].map(m => m.innerText).join(' '))`, 15000);
  check('after sending, the dialog says to approve the prompt on the phone', waiting, (await modalText()).slice(0, 300));
  const ref = [...stub.state.collects.values()].at(-1)?.referenceId;
  check('the gateway holds one collect for the number', !!ref && stub.state.collects.size === 1 && [...stub.state.collects.values()][0].phoneNumber === '256772555111');
  await settle(ref, 'Succeeded');
  const closed = await t.waitFor(`![...document.querySelectorAll('.q-modal')].some(m => m.offsetParent !== null && /Send prompt|Approve the prompt/.test(m.innerText))`, 20000);
  check('when the webhook lands the dialog closes itself', closed);
  await t.sleep(1000);
  check('the module row now reads Active', await t.eval(`(() => { const row = [...document.querySelectorAll('.qm-main tr')].find(r => r.innerText.includes(${JSON.stringify(target.name)})); return /Active/i.test(row?.innerText ?? ''); })()`));

  // ---------------------------------------------------------------------------------------------------
  hdr('3. Cards through the gateway: name and email required');
  await api(SA, 'PUT', '/api/v1/platform/payments/gateway', { enabled: true, baseUrl: stub.url, apiKey: MASK, cardsEnabled: true });
  const second = catalog.find(c => c.code !== target.code && !(owned.some(m => m.moduleCode === c.code && m.purchased)));
  if (!second) {
    check('a second module to open the card dialog on', false, 'the tenant owns every module');
  } else {
    await t.goto(BASE + '/billing?tab=modules');
    await t.waitFor(`!!document.querySelector('.qm-main table.data-table')`, 20000);
    await t.sleep(800);
    await t.eval(`(() => { const row = [...document.querySelectorAll('.qm-main tr')].find(r => r.innerText.includes(${JSON.stringify(second.name)}));
      [...(row?.querySelectorAll('button') ?? [])].find(x => /^(Add|Pay)$/.test(x.innerText.trim()))?.click(); })()`);
    await t.waitFor(`/Send prompt/.test([...document.querySelectorAll('.q-modal')].map(m => m.innerText).join(' '))`, 15000);
    await t.sleep(600);
    const cardOptions = await openPayBy();
    check('with cards on, Card is offered too', cardOptions.some(o => /Card/.test(o)), JSON.stringify(cardOptions));
    await press(`[...document.querySelectorAll('.q-select__dropdown .q-select__option')].find(o => /Card/.test(o.innerText))`);
    await t.waitFor(`/Name on the card/.test([...document.querySelectorAll('.q-modal')].map(m => m.innerText).join(' '))`, 5000);
    dialog = await modalText();
    check('the card form asks for the name and the receipt email', /Name on the card/.test(dialog) && /Email for the receipt/.test(dialog), dialog.slice(0, 300));
    await t.clickText('Continue to card payment', '.q-modal button');
    await t.sleep(600);
    dialog = await modalText();
    check('both are required, with their own messages', /Enter the name on the card/.test(dialog) && /Enter an email address for the receipt/.test(dialog), dialog.slice(0, 400));
    await t.setValue('.q-modal input[type=email]', 'not-an-email');
    await t.clickText('Continue to card payment', '.q-modal button');
    await t.sleep(500);
    check('a malformed email is refused', /Enter a valid email address/.test(await modalText()));
    check('still nothing reached the gateway for it', stub.state.collects.size === 1);
    await t.clickText('Cancel', '.q-modal button');
  }
  await api(SA, 'PUT', '/api/v1/platform/payments/gateway', { enabled: true, baseUrl: stub.url, apiKey: MASK, cardsEnabled: false });

  // ---------------------------------------------------------------------------------------------------
  hdr('4. Paying an open invoice from the Invoices tab');
  if (!second) {
    check('an invoice to pay', false, 'no second module');
  } else {
    const pending = (await api(AD, 'POST', `/api/v1/modules/${second.code}/purchase`, { billingCycle: 'Monthly', method: 'mobile', phoneNumber: '0772 555 111' })).json;
    await t.goto(BASE + '/billing?tab=invoices');
    await t.waitFor(`!!document.querySelector('.qm-main table.data-table')`, 20000);
    await t.sleep(800);
    const payButtons = await t.eval(`[...document.querySelectorAll('.qm-main tr')].filter(r => /Open/i.test(r.innerText) && [...r.querySelectorAll('button')].some(b => b.innerText.trim() === 'Pay')).length`);
    check('an Open invoice carries a Pay button', payButtons >= 1);
    check('a Paid invoice does not', await t.eval(`![...document.querySelectorAll('.qm-main tr')].some(r => /\\bPaid\\b/i.test(r.innerText) && [...r.querySelectorAll('button')].some(b => b.innerText.trim() === 'Pay'))`));
    await t.eval(`[...document.querySelectorAll('.qm-main tr')].find(r => /Open/i.test(r.innerText) && [...r.querySelectorAll('button')].some(b => b.innerText.trim() === 'Pay'))?.querySelector('button')?.click()`);
    await t.waitFor(`/Send prompt/.test([...document.querySelectorAll('.q-modal')].map(m => m.innerText).join(' '))`, 15000);
    check('the pay dialog is prefilled with the renewal number', await t.eval(`document.querySelector('.q-modal input[type=tel]')?.value`) === '0772 555 111');
    await t.clickText('Send prompt', '.q-modal button');
    await t.waitFor(`/Approve the prompt/.test([...document.querySelectorAll('.q-modal')].map(m => m.innerText).join(' '))`, 15000);
    check('a prompt already out for the invoice is reused, not sent again', stub.state.collects.size === 2);
    await settle(pending.referenceId, 'Succeeded');
    const paid = await t.waitFor(`![...document.querySelectorAll('.q-modal')].some(m => m.offsetParent !== null && /Approve the prompt/.test(m.innerText))`, 20000);
    check('the dialog closes when the gateway confirms', paid);
    await api(AD, 'DELETE', `/api/v1/modules/${second.code}`);
  }

  // ---------------------------------------------------------------------------------------------------
  hdr('5. The platform Payments page');
  await login(t, 'superadmin', 'admin');
  await t.goto(BASE + '/platform/payments');
  await t.waitFor(`/Gateway address/.test(document.querySelector('.qm-main')?.innerText ?? '')`, 20000);
  await t.sleep(800);
  const gw = await main();
  check('two tabs: Gateway and Reconciliation', /Gateway/.test(gw) && /Reconciliation/.test(gw));
  check('the sidebar entry is highlighted', await t.eval(`!![...document.querySelectorAll('.qm-sidebar a.active')].find(a => /Payments/.test(a.innerText))`));
  check('the tiles say Mobile Money is available and the webhook is registered', /Available\s*Mobile Money|Mobile Money\s*Available/i.test(gw) && /Registered/i.test(gw), gw.slice(0, 400));
  check('Stripe reads Off', /Off\s*Stripe|Stripe\s*Off/i.test(gw), gw.slice(0, 500));
  check('the API key is shown as the mask, never the key', await t.eval(`[...document.querySelectorAll('.qm-main input[type=password]')].some(i => i.value === '${MASK}')`) && !gw.includes(STUB_KEY));
  await t.setValue('.qm-main input[type=url]', 'http://sacc.ug');
  await t.sleep(300);
  await t.clickText('Save settings', 'button');
  await t.waitFor(`/must start with https/.test(document.querySelector('.qm-main')?.innerText ?? '')`, 5000);
  check('a plain-http public address is refused inline before saving', /must start with https/.test(await main()), (await main()).slice(0, 500));
  await t.setValue('.qm-main input[type=url]', stub.url);
  await t.clickText('Check connection', 'button');
  await t.sleep(1500);
  check('Check connection says the key works', /accepts the key/.test(await main()));
  await t.setValue('.qm-main input[type=tel]', '0772 555 111');
  await t.clickText('Send UGX 500 prompt', 'button');
  await t.sleep(1200);
  const testRef = [...stub.state.collects.values()].at(-1)?.referenceId;
  check('the test prompt reached the gateway for UGX 500', stub.collect(testRef)?.amount === 500);
  await settle(testRef, 'Succeeded');
  const webhookSeen = await t.waitFor(`/Webhook\\s*Received/i.test(document.querySelector('.qm-main')?.innerText ?? '')`, 25000);
  check('the page reports the gateway confirmed it and the webhook arrived', webhookSeen && /Succeeded/i.test(await main()), (await main()).slice(-500));

  // A held payment to decide in the page.
  const mineNow = await mine();
  const heldTarget = catalog.find(c => !mineNow.some(m => m.moduleCode === c.code && m.status === 'Active'));
  let heldRef = null;
  if (heldTarget) {
    heldRef = (await api(AD, 'POST', `/api/v1/modules/${heldTarget.code}/purchase`, { billingCycle: 'Monthly', method: 'mobile', phoneNumber: '0772 555 111' })).json?.referenceId;
    await fetch(stub.url + '/__stub/settle', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ referenceId: heldRef, state: 'Succeeded', confirmedAmount: stub.collect(heldRef).amount - 1000 }) });
    await new Promise(r => setTimeout(r, 1500));
  }

  await t.goto(BASE + '/platform/payments?tab=reconciliation');
  await t.waitFor(`/Prompts sent/i.test(document.querySelector('.qm-main')?.innerText ?? '')`, 20000);
  if (heldRef) {
    await t.sleep(600);
    check('a held payment shows under Needs a decision', /Needs a decision/i.test(await main()), (await main()).slice(0, 400));
    await t.eval(`[...document.querySelectorAll('.qm-main tr')].find(r => r.innerText.includes(${JSON.stringify(heldRef.replace(/-/g, '').slice(0, 8))}))?.querySelector('button')?.click()`);
    await t.waitFor(`/Decide a held payment/.test([...document.querySelectorAll('.q-modal')].map(m => m.innerText).join(' '))`, 8000);
    check('the dialog says the decision is final', /This is final/.test(await modalText()));
    await t.clickText('Record decision', '.q-modal button');
    await t.sleep(600);
    const refusedText = await modalText();
    check('recording with nothing chosen names both fields', /Choose what happened/.test(refusedText) && /Say what you checked/.test(refusedText), refusedText.slice(0, 400));
    await press(`[...document.querySelectorAll('.q-modal .q-select')].at(-1)`);
    await t.waitFor(`!!document.querySelector('.q-select__dropdown')`, 4000);
    await press(`[...document.querySelectorAll('.q-select__dropdown .q-select__option')].find(o => /was received/.test(o.innerText))`);
    await t.setValue('.q-modal textarea', 'CRMPro receipt shows the full amount was paid');
    await t.sleep(300);
    await t.clickText('Record decision', '.q-modal button');
    const done = await t.waitFor(`![...document.querySelectorAll('.q-modal')].some(m => m.offsetParent !== null && /Decide a held payment/.test(m.innerText))`, 10000);
    check('recording closes the dialog', done);
    check('the payment is now paid', (await api(AD, 'GET', `/api/v1/billing/payments/${heldRef}`)).json?.state === 'Succeeded');
    await t.sleep(800);
    check('and the list shows who decided it', /Marked received by/.test(await main()));
    await api(AD, 'DELETE', `/api/v1/modules/${heldTarget.code}`);
  } else {
    check('a module to hold a payment for', false, 'the tenant owns every module');
  }
  await t.sleep(800);
  const rec = await main();
  check('reconciliation shows the figures and a by-day table', /Confirmed/i.test(rec) && /By day/i.test(rec) && /Latest payments/i.test(rec), rec.slice(0, 400));
  check('phone numbers there are masked', !/0772 555 111/.test(rec) && /0772 ••• 111/.test(rec));

  check('no Blazor error bar at any point', !(await errorBar()));
  check('no uncaught exception in the console', t.consoleErrors.filter(e => !/favicon|404/.test(e)).length === 0, t.consoleErrors.join(' | '));
} catch (e) {
  check('the suite ran to the end', false, e?.stack ?? e);
} finally {
  if (cleanup.module) await api(AD, 'DELETE', `/api/v1/modules/${cleanup.module}`);
  if (cleanup.renewal) await api(AD, 'PUT', '/api/v1/billing/renewal-number', { phoneNumber: cleanup.renewal });
  else if (cleanup.renewal === null) await api(AD, 'DELETE', '/api/v1/billing/renewal-number');
  await api(SA, 'PUT', '/api/v1/platform/payments/gateway', { enabled: false, baseUrl: cleanup.gatewayUrl ?? 'https://sacc.ug', apiKey: MASK, cardsEnabled: false });
  t.close();
  await stub.close();
  const line = `  payments-ui: ${pass} passed, ${fail} failed`;
  console.log(line); post(line);
  process.exitCode = fail ? 1 : 0;
}
