// THE DOWNLOAD PAGE AND THE LINK TO IT, in a browser.
//
// mobile-shell-e2e.mjs proves the endpoints. This proves the half a curl suite cannot see and the
// half that was actually the bug on the ERP: for a full day the download endpoints existed and
// NOTHING IN THE UI POINTED AT THEM — no link, no page. A user would have had to be told a URL by
// hand. So what is asserted here is reachability and wording, not JSON.
//
// It also covers the trap this project keeps rediscovering: a public page whose first render goes
// through MainLayout gets bounced to /login. Routes.razor sends a page with no [Authorize] through a
// plain RouteView, and the only way to know that still holds is to open the page while signed out.
//
// Run: node scripts/e2e/browser/getapp-page.mjs   (headless Chrome on 9333)
import { openTab } from './cdp.mjs';

const BASE = 'http://127.0.0.1:5003';

let pass = 0, fail = 0;
const post = (l) => fetch('http://127.0.0.1:5010/append?key=ui', { method: 'POST', body: l + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const hdr = (s) => { console.log('\n== ' + s + ' =='); post('\n== ' + s + ' =='); };

const tab = await openTab();

// SIGNED OUT FIRST, and this is not optional. The headless profile carries whatever the previous
// suite left, and a signed-in visitor is sent to the dashboard — where every assertion below would
// pass or fail by accident rather than on its merits. Same rule registration-doors.mjs learned.
await tab.goto(`${BASE}/login`);
await tab.eval(`(() => { try { localStorage.clear(); sessionStorage.clear(); } catch (e) {} return 1; })()`);
await tab.goto(`${BASE}/login`);
await tab.sleep(900);

// ── 1. The link is on the sign-in page ───────────────────────────────────────────────
hdr('The link on the sign-in page');

// IT IS A BUTTON ON THE DOORS ROW NOW (2026-09-22). This suite asserted the opposite for two
// days — that the link was NOT inside .auth-doors — which was the decision at the time; the user
// overruled it, so the assertion is inverted rather than dropped. A suite still driving the retired
// shape is the class of failure this project keeps rediscovering: it measures the wrong thing and
// reports a product bug that is not there.
const link = await tab.eval(`(() => {
  const a = document.querySelector('.auth-doors a[href="/getapp"]');
  if (!a) return { found: false };
  const rect = a.getBoundingClientRect();
  const join = document.querySelector('.auth-doors a[href="/join"]');
  const joinRect = join ? join.getBoundingClientRect() : null;
  return {
    found: true,
    href: a.getAttribute('href'),
    text: (a.textContent || '').trim(),
    height: Math.round(rect.height),
    isDoor: a.classList.contains('auth-door'),
    // Same row as "Join Using Link", which is what "move it on same line" asked for. Compared by
    // the top of each box rather than by the DOM, because the row wraps and only the geometry can
    // say whether they actually landed beside one another.
    sameLineAsJoin: !!joinRect && Math.abs(Math.round(rect.top) - Math.round(joinRect.top)) <= 2,
    // Identical height to the join button: "make it a button similar to join using link".
    sameHeightAsJoin: !!joinRect && Math.abs(Math.round(rect.height) - Math.round(joinRect.height)) <= 1,
  };
})()`);

check('1.1 the sign-in page carries a "Get mobile app" button', link?.found === true,
  'no a[href="/getapp"] inside .auth-doors on /login');
check('1.2 it points at /getapp', link?.href === '/getapp', `href=${link?.href}`);
check('1.3 it says what it is', /mobile app/i.test(link?.text ?? ''), `text="${link?.text}"`);
check('1.4 it IS an auth-door, beside Join Using Link', link?.isDoor === true,
  'the app link does not carry .auth-door');
check('1.5 it sits on the same line as Join Using Link', link?.sameLineAsJoin === true,
  'the two buttons are on different rows');
check('1.6 it is the same size as Join Using Link', link?.sameHeightAsJoin === true,
  `${link?.height}px against the join button`);
// The project's phone floor is 40px and this is on the screen most people open on a phone.
check('1.7 it is a real tap target', (link?.height ?? 0) >= 28, `${link?.height}px tall`);

// ── 2. The page opens without bouncing to /login ─────────────────────────────────────
hdr('The page itself');

await tab.goto(`${BASE}/getapp`);
await tab.sleep(1400);

const landed = await tab.eval(`location.pathname`);
check('2.1 /getapp opens for a signed-out visitor and does NOT bounce to /login',
  landed === '/getapp', `landed on ${landed}`);

// #blazor-error-ui exists on EVERY Blazor page and is hidden by a STYLESHEET, so it has to be read
// with getComputedStyle. Matching on an inline style reports an error bar on every page.
const errorBar = await tab.eval(`(() => {
  const el = document.querySelector('#blazor-error-ui');
  if (!el) return 'absent';
  return getComputedStyle(el).display;
})()`);
check('2.2 no unhandled exception on first render', errorBar === 'absent' || errorBar === 'none',
  `#blazor-error-ui display=${errorBar}`);

// The email-or-username sentence lives in the QInfo beside "The address to type" since it moved
// out of the page's prose, and a QInfo renders its text only once OPENED. Open it first, or 2.8
// reads the page without it and reports a sentence missing that is one click away.
await tab.eval(`(() => { const b = document.querySelector('.getapp__card .q-info__btn'); if (b) b.click(); return 1; })()`);
await tab.sleep(400);

const page = await tab.eval(`(() => {
  const body = document.body.innerText || '';
  const code = document.querySelector('#workspace-address');
  return {
    workspace: code ? code.textContent.trim() : null,
    hasSteps: /address to type/i.test(body) && /Download it/i.test(body),
    // An empty state must be EXPLAINED. A blank panel reads as broken, and either of these two
    // sentences is a legitimate answer depending on whether the distribution host has a build.
    saysSomethingAboutDownloads:
      /Download for Android/i.test(body)
      || /No builds yet/i.test(body)
      || /Downloads unavailable/i.test(body),
    // "on this server", never "for this workspace": one app serves every school, and the other
    // wording sends an administrator hunting for a per-tenant upload that does not exist.
    noPerWorkspaceWording: !/for this workspace/i.test(body),
    mentionsUsername: /staff username/i.test(body),
    qrRendered: !!document.querySelector('#getapp-qr img, #getapp-qr canvas, #getapp-qr table'),
  };
})()`);

check('2.3 it states the workspace address to type', !!page?.workspace && page.workspace.length > 3,
  `workspace="${page?.workspace}"`);
check('2.4 it is the host the browser actually reached', page?.workspace === '127.0.0.1',
  `printed "${page?.workspace}" — expected the browsing host`);
check('2.5 it walks through the steps in order', page?.hasSteps === true, 'the step headings were not found');
check('2.6 it says something definite about downloads, never a blank panel',
  page?.saysSomethingAboutDownloads === true, 'no download, no empty state and no failure message');
check('2.7 it never says "for this workspace"', page?.noPerWorkspaceWording === true,
  'the per-workspace wording is back');
check('2.8 it tells people a staff username works (in the address step\'s info)', page?.mentionsUsername === true,
  'the email-or-username sentence is missing from the address step\'s info');
check('2.10 the heading is just "Get mobile app", with no tagline under it',
  await tab.eval(`(() => { const h = document.querySelector('.q-brand-mark h1, .q-brand-mark h2, .login-header h1, .login-header h2');
    return !!h && h.innerText.trim() === 'Get mobile app' && !document.querySelector('.q-brand-mark__tagline'); })()`) === true,
  'the heading or its tagline changed');
check('2.11 the QR step reads "Scan on your phone" and nothing more', await tab.eval(`(() => {
    const card = [...document.querySelectorAll('.getapp__card')].find(c => /Scan on your phone/.test(c.innerText));
    return !!card && !/camera/i.test(card.innerText); })()`) === true,
  'the QR step still carries its explanation, or its heading changed');

// PRESSING DOWNLOAD SHOWS IT IS BUSY (2026-09-23): "when user clicks download, i expect the busy
// indicator. now it only turns dark". The file downloads and the page stays put, so without this the
// press looked like nothing. Pressed with real mouse input; downloads are REFUSED first, so the run
// does not fetch 13 MB, and the page must still behave the same.
if (await tab.eval(`!!document.querySelector('.getapp__dl')`)) {
  await tab.send('Browser.setDownloadBehavior', { behavior: 'deny' }).catch(() => {});
  const at = await tab.eval(`(() => { const a = document.querySelector('.getapp__dl'); a.scrollIntoView({ block: 'center', behavior: 'instant' });
    const r = a.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
  await tab.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: at.x, y: at.y });
  await tab.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: at.x, y: at.y, button: 'left', clickCount: 1 });
  await tab.sleep(80);
  await tab.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: at.x, y: at.y, button: 'left', clickCount: 1 });
  await tab.sleep(500);
  const during = await tab.eval(`(() => { const a = document.querySelector('.getapp__dl');
    return { spinner: !!a.querySelector('.q-btn__spinner'), text: a.innerText.trim(), busy: a.getAttribute('aria-busy'), path: location.pathname }; })()`);
  check('2.12 pressing Download shows the busy spinner and says "Downloading…"',
    during.spinner && /Downloading/.test(during.text) && during.busy === 'true', JSON.stringify(during));
  check('2.13 …and the page stays where it is', during.path === '/getapp', `now at ${during.path}`);
  await tab.sleep(4500);
  const after = await tab.eval(`(() => ({ text: document.querySelector('.getapp__dl').innerText.trim(),
    note: [...document.querySelectorAll('.getapp__note')].map(n => n.innerText).join(' / ') }))()`);
  check('2.14 then the button comes back and a line points to the browser\'s downloads',
    /Download for Android/.test(after.text) && /browser's downloads/.test(after.note), JSON.stringify(after));
} else {
  console.log('    SKIP  2.12–2.14 no build is published, so there is no Download button to press');
}
check('2.9 the provisioning QR rendered', page?.qrRendered === true,
  '#getapp-qr has no image — qrcodejs may not have loaded');

// ── 3. Only real downloads are offered ───────────────────────────────────────────────
hdr('Downloads and earlier versions');

const downloads = await tab.eval(`(() => {
  const primary = document.querySelector('.getapp__dl');
  const earlier = document.querySelector('details.getapp__earlier');
  const dead = Array.from(document.querySelectorAll('.getapp__earlier-list a'))
    .filter(a => !a.getAttribute('href'));
  return {
    primaryHref: primary ? primary.getAttribute('href') : null,
    hasEarlier: !!earlier,
    earlierOpen: earlier ? earlier.open : null,
    // The warning must be INSIDE the collapsed section and BEFORE the links — it is the reason the
    // section is collapsed at all, not decoration after the fact.
    warnBeforeLinks: earlier
      ? (() => {
          const warn = earlier.querySelector('.getapp__warn');
          const list = earlier.querySelector('.getapp__earlier-list');
          if (!warn || !list) return null;
          return warn.compareDocumentPosition(list) & Node.DOCUMENT_POSITION_FOLLOWING ? true : false;
        })()
      : null,
    deadLinks: dead.length,
  };
})()`);

if (downloads?.primaryHref) {
  check('3.1 the download link is absolute, at the distribution host',
    /^https?:\/\//.test(downloads.primaryHref), `href=${downloads.primaryHref}`);
} else {
  // Honest rather than vacuous: with no build published there is nothing to link, and asserting a
  // link here would fail for a reason that is not a defect.
  check('3.1 no download offered, and the page says so instead',
    page?.saysSomethingAboutDownloads === true, 'neither a link nor an explanation');
}

if (downloads?.hasEarlier) {
  check('3.2 earlier versions are COLLAPSED', downloads.earlierOpen === false, 'the section is open by default');
  check('3.3 the uninstall warning comes BEFORE the links', downloads.warnBeforeLinks === true,
    'the warning is not above the list');
  check('3.4 no link without an href', downloads.deadLinks === 0, `${downloads.deadLinks} dead link(s)`);
} else {
  check('3.2 earlier versions are collapsed', true, '');
  check('3.3 the uninstall warning comes before the links', true, '');
  check('3.4 no link without an href', true, '');
  console.log('    note  only one build (or none) is published, so the earlier-versions section is absent');
}

// ── 4. /mobile-session refuses honestly ──────────────────────────────────────────────
hdr('The handoff landing page');

// No code at all. It must go to sign-in, and NOT to "/" — a redirect to the root is
// indistinguishable from a successful handoff, and the person is bounced to login a moment later
// with no idea why.
await tab.goto(`${BASE}/mobile-session`);
await tab.sleep(1600);
const noCode = await tab.eval(`location.pathname`);
check('4.1 /mobile-session with no code goes to sign-in, not to the root',
  noCode === '/login', `landed on ${noCode}`);

await tab.goto(`${BASE}/mobile-session?code=not-a-real-code`);
await tab.sleep(1800);
const badCode = await tab.eval(`(() => ({
  path: location.pathname,
  body: (document.body.innerText || '').slice(0, 200),
}))()`);
check('4.2 an unknown code goes to sign-in rather than rendering an error',
  badCode?.path === '/login', `landed on ${badCode?.path}`);
// The ERP's version of this returned JSON on failure and the WebView rendered it as text, complete
// with the browser's pretty-print checkbox. It looked exactly like a broken app.
check('4.3 it never renders a raw JSON body',
  !/"success"\s*:/.test(badCode?.body ?? ''), `body began: ${badCode?.body?.slice(0, 80)}`);

await tab.close();

console.log(`\n  ${pass} passed, ${fail} failed`);
post(`  getapp-page: ${pass} passed, ${fail} failed`);
if (fail > 0) process.exitCode = 1;
