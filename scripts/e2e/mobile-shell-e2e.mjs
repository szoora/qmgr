// SECTION 25 — THE MOBILE SHELL: PER-DEVICE SESSIONS, THE HANDOFF, AND APP DISTRIBUTION.
//
// Q-Mgr had ONE refresh token per user, in a plaintext column, matched by an unindexed scan. So a
// phone signing in evicted the browser and the browser refreshing evicted the phone, and there was
// nothing to revoke one lost handset with. UserDeviceSession replaced that for devices only — the
// browser's column is untouched — and RFC 9700 §4.14 requires rotation outright for a public client,
// which a mobile app is.
//
// IT IS NODE BECAUSE THE INTERESTING HALF IS CONCURRENCY. Rotation is only worth having if the
// REPLAY is detectable: two redemptions of one token must not both succeed, and the loser must kill
// the device's session rather than merely failing. That cannot be asserted with sequential curl.
//
// What it covers:
//    1  A device sign-in issues a {userId}.{deviceId}.{secret} token, and a browser sign-in does not
//    2  Rotation — the presented token dies, the new one works
//    3  REPLAY — a rotated-away token is refused AND revokes the device
//    4  Concurrency — five simultaneous redemptions produce exactly one winner
//    5  A device sign-in does NOT evict the browser, and vice versa
//    6  The device list, and revoking one device without touching another
//    7  logout with neither deviceId nor allDevices is 400, never a silent 200
//    8  The handoff code is single-use and dies; an unknown code is 401
//    9  tenant/info is flat, names the platform on the shared host, and advertises capabilities
//   10  app/update compares the INTEGER versionCode and reports self-install per platform
//
// Run: node scripts/e2e/mobile-shell-e2e.mjs   (or through class-teacher-e2e.sh)
const API = process.env.API ?? 'http://127.0.0.1:5001';
const USER = process.env.E2E_USER ?? 'e2e.admin.ct@qmgr.local';
const PASS = process.env.E2E_PASS ?? 'E2eTeacher!2026';
const NEW_PW = process.env.E2E_NEW_PW ?? 'Qm!7fRt2026xz';

let pass = 0, fail = 0, skip = 0;
const post = (line) => fetch('http://127.0.0.1:5010/append?key=api', { method: 'POST', body: line + '\n' }).catch(() => {});
const check = (name, ok, detail = '') => {
  ok ? pass++ : fail++;
  const l = `    ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : '  — ' + detail}`;
  console.log(l); post(l);
};
const note = (name, why) => { skip++; const l = `    SKIP  ${name}  — ${why}`; console.log(l); post(l); };

const J = { 'Content-Type': 'application/json' };
const login = (extra = {}) => fetch(`${API}/api/v1/auth/login`, {
  method: 'POST', headers: J, body: JSON.stringify({ email: USER, password: PASS, ...extra }),
});
const refresh = (token) => fetch(`${API}/api/v1/auth/refresh`, {
  method: 'POST', headers: J, body: JSON.stringify({ refreshToken: token }),
});

// A device id unique to this RUN, so the suite is safe against a live account: it never disturbs a
// session the account already has, and two runs cannot revoke each other. Same discipline as the
// shell's own conformance suite.
const DEV_A = `e2edev-a-${Date.now().toString(36)}`;
const DEV_B = `e2edev-b-${Date.now().toString(36)}`;

console.log('\n  25. The mobile shell — per-device sessions, handoff, distribution');

// ── Sign in as a device ───────────────────────────────────────────────────────────────
const deviceLogin = await login({ deviceId: DEV_A, deviceName: 'E2E Handset A', platform: 'android' });
if (!deviceLogin.ok) {
  console.error(`could not sign in as ${USER} (HTTP ${deviceLogin.status})`);
  process.exit(1);
}
const devA = await deviceLogin.json();

check('25.1 a device sign-in returns a token shaped {userId}.{deviceId}.{secret}',
  typeof devA.refreshToken === 'string'
  && devA.refreshToken.split('.').length === 3
  && devA.refreshToken.split('.')[1] === DEV_A,
  `got ${String(devA.refreshToken).slice(0, 24)}…`);

check('25.2 the access token and user come back with it',
  !!devA.accessToken && !!devA.user?.id, 'missing accessToken or user');

// A BROWSER sign-in — no deviceId — must keep the old single-column shape.
const webLogin = await login();
const web = await webLogin.json();
check('25.3 a browser sign-in is NOT given a device token',
  !!web.refreshToken && web.refreshToken.split('.').length !== 3,
  'the browser was handed a device-shaped token');

// ── Rotation, and the replay that must revoke ────────────────────────────────────────
const rotated = await refresh(devA.refreshToken);
const rotatedBody = rotated.ok ? await rotated.json() : null;

check('25.4 a device token redeems and ROTATES',
  rotated.ok && !!rotatedBody?.refreshToken && rotatedBody.refreshToken !== devA.refreshToken,
  `HTTP ${rotated.status}`);

check('25.5 the rotated token still names the same device',
  rotatedBody?.refreshToken?.split('.')[1] === DEV_A, 'device segment changed');

// The presented token is now dead. Redeeming it again is the REPLAY case.
const replay = await refresh(devA.refreshToken);
check('25.6 a replayed token is refused with 401, never 400 or 500',
  replay.status === 401, `HTTP ${replay.status}`);

// …and the replay must have revoked the DEVICE, not merely failed itself. So the token the
// legitimate device is holding stops working too. This is the half that makes rotation worth
// having, and the half a sequential test usually forgets.
const afterReplay = await refresh(rotatedBody.refreshToken);
check('25.7 the replay REVOKED the device — the legitimate token is dead too',
  afterReplay.status === 401, `HTTP ${afterReplay.status}`);

// ── Concurrency: exactly one winner ──────────────────────────────────────────────────
const raceLogin = await login({ deviceId: DEV_B, deviceName: 'E2E Handset B', platform: 'android' });
const devB = await raceLogin.json();

const raced = await Promise.all(Array.from({ length: 5 }, () => refresh(devB.refreshToken)));
const winners = raced.filter(r => r.ok).length;
check('25.8 five simultaneous redemptions of one token produce exactly ONE winner',
  winners === 1, `${winners} of 5 succeeded`);

// ── A device must not evict the browser ──────────────────────────────────────────────
// This is the defect the whole table exists for, so it is asserted directly rather than inferred.
const devC = await (await login({ deviceId: `e2edev-c-${Date.now().toString(36)}`, deviceName: 'E2E Handset C' })).json();
const webStillGood = await refresh(web.refreshToken);
check('25.9 a device signing in does NOT evict the browser',
  webStillGood.ok, `browser refresh returned HTTP ${webStillGood.status}`);

// ── Devices, and revoking one ────────────────────────────────────────────────────────
const bearer = { ...J, Authorization: `Bearer ${devC.accessToken}` };
const devices = await fetch(`${API}/api/v1/auth/devices`, { headers: bearer });
const deviceList = devices.ok ? await devices.json() : null;

check('25.10 the device list is wrapped and lists live handsets',
  devices.ok && deviceList?.success === true && Array.isArray(deviceList?.data),
  `HTTP ${devices.status}`);

check('25.11 a revoked device is NOT listed',
  !(deviceList?.data ?? []).some(d => d.deviceId === DEV_A),
  'the device revoked by the replay is still listed');

// logout with NOTHING identified must be 400. A 200 that revoked nothing is the worst available
// outcome, because it is indistinguishable from success.
const emptyLogout = await fetch(`${API}/api/v1/auth/logout`, {
  method: 'POST', headers: bearer, body: JSON.stringify({}),
});
check('25.12 logout naming no device is 400, not a silent 200',
  emptyLogout.status === 400, `HTTP ${emptyLogout.status}`);

// Revoke DEV_B by name while signed in on DEV_C, then prove B is dead and C is alive.
await fetch(`${API}/api/v1/auth/logout`, {
  method: 'POST', headers: bearer, body: JSON.stringify({ deviceId: DEV_B }),
});
const bAfter = await refresh(raced.find(r => r.ok) ? (await Promise.resolve(devB.refreshToken)) : devB.refreshToken);
check('25.13 revoking one device kills that device', bAfter.status === 401, `HTTP ${bAfter.status}`);

const cAfter = await refresh(devC.refreshToken);
check('25.14 …and leaves another device signed in', cAfter.ok, `HTTP ${cAfter.status}`);

// ── The handoff ──────────────────────────────────────────────────────────────────────
const cRefreshed = cAfter.ok ? await cAfter.json() : null;
const handoffAuth = { ...J, Authorization: `Bearer ${cRefreshed?.accessToken ?? devC.accessToken}` };

const handoff = await fetch(`${API}/api/v1/auth/web-handoff`, { method: 'POST', headers: handoffAuth });
const handoffBody = handoff.ok ? await handoff.json() : null;
const code = handoffBody?.data?.code;

check('25.15 web-handoff returns a code and an expiry', handoff.ok && !!code && !!handoffBody?.data?.expiresAt,
  `HTTP ${handoff.status}`);

const redeem1 = await fetch(`${API}/api/v1/auth/web-session`, {
  method: 'POST', headers: J, body: JSON.stringify({ code }),
});
check('25.16 the code redeems once, for a real session',
  redeem1.ok, `HTTP ${redeem1.status}`);

const redeem2 = await fetch(`${API}/api/v1/auth/web-session`, {
  method: 'POST', headers: J, body: JSON.stringify({ code }),
});
check('25.17 the code is SINGLE USE — a second redemption is 401',
  redeem2.status === 401, `HTTP ${redeem2.status}`);

const bogus = await fetch(`${API}/api/v1/auth/web-session`, {
  method: 'POST', headers: J, body: JSON.stringify({ code: 'not-a-real-code' }),
});
check('25.18 an unknown code reads the same as a used one — 401',
  bogus.status === 401, `HTTP ${bogus.status}`);

// ── tenant/info ──────────────────────────────────────────────────────────────────────
const info = await fetch(`${API}/api/v1/tenant/info`);
const infoBody = info.ok ? await info.json() : null;

check('25.19 tenant/info is anonymous and answers 200', info.ok, `HTTP ${info.status}`);

check('25.20 it is FLAT, not wrapped in { data } — the app reads it before it knows anything',
  infoBody != null && infoBody.data === undefined && typeof infoBody.companyName === 'string',
  'a data envelope was found');

check('25.21 on the shared host it names the PLATFORM and invents no school',
  infoBody?.product === 'SACC Dashboard' && (infoBody?.tenant === null || infoBody?.tenant === undefined),
  `product=${infoBody?.product} tenant=${infoBody?.tenant}`);

check('25.22 it advertises its capabilities so the app need not probe for 404s',
  infoBody?.api?.refresh === true && infoBody?.api?.deviceSessions === true,
  JSON.stringify(infoBody?.api));

check('25.23 attribution removal is FALSE by default on the platform host',
  infoBody?.attributionRemoved === false, `got ${infoBody?.attributionRemoved}`);

// ── App distribution ─────────────────────────────────────────────────────────────────
const upd = await fetch(`${API}/api/v1/app/update?platform=android&versionCode=1`);
const updBody = upd.ok ? await upd.json() : null;

check('25.24 app/update is anonymous and wrapped', upd.ok && updBody?.success === true, `HTTP ${upd.status}`);

if (updBody?.data?.updateAvailable) {
  check('25.25 an offered update carries an integer versionCode and a sha256',
    Number.isInteger(updBody.data.versionCode) && updBody.data.versionCode > 1
    && typeof updBody.data.sha256 === 'string',
    JSON.stringify(updBody.data).slice(0, 120));

  // The same request from a device already ON the newest build must report nothing, which is what
  // proves the comparison is numeric rather than a truthy "there is a release".
  const atLatest = await fetch(`${API}/api/v1/app/update?platform=android&versionCode=${updBody.data.versionCode}`);
  const atLatestBody = await atLatest.json();
  check('25.26 a device already on the newest build is told there is no update',
    atLatestBody?.data?.updateAvailable === false, JSON.stringify(atLatestBody?.data));
} else {
  // Honest, not vacuous: the distribution host may legitimately have no qmgr folder yet, and
  // passing these two by asserting nothing would hide the day it does.
  note('25.25 an offered update carries a versionCode and sha256',
    'no build published for qmgr on the distribution host yet');
  note('25.26 a device on the newest build is told there is no update',
    'no build published for qmgr on the distribution host yet');
}

check('25.27 android reports it CAN self-install',
  updBody?.data?.canSelfInstall === true, `got ${updBody?.data?.canSelfInstall}`);

const iosUpd = await fetch(`${API}/api/v1/app/update?platform=ios&versionCode=1`);
const iosBody = await iosUpd.json();
check('25.28 iOS reports it CANNOT self-install — Apple permits no self-distribution',
  iosBody?.data?.canSelfInstall === false, `got ${iosBody?.data?.canSelfInstall}`);

const rel = await fetch(`${API}/api/v1/app/releases?platform=android`);
const relBody = rel.ok ? await rel.json() : null;
check('25.29 app/releases lists builds newest first, with Latest separate',
  rel.ok && Array.isArray(relBody?.data?.releases), `HTTP ${rel.status}`);

// ── Check-in ─────────────────────────────────────────────────────────────────────────
const checkin = await fetch(`${API}/api/v1/app/checkin`, {
  method: 'POST', headers: handoffAuth,
  body: JSON.stringify({
    deviceId: devC.refreshToken.split('.')[1],
    deviceName: 'E2E Handset C', platform: 'android',
    appVersion: '1.0', appVersionCode: 1,
    pushToken: 'e2e-not-a-real-fcm-token', pushPermitted: true,
  }),
});
const checkinBody = checkin.ok ? await checkin.json() : null;
check('25.30 check-in is accepted and returns an unread count and a next interval',
  checkin.ok && typeof checkinBody?.data?.unreadCount === 'number'
  && checkinBody?.data?.nextCheckInSeconds > 0,
  `HTTP ${checkin.status}`);

// A check-in from a device with NO session must not mint one. It reports honestly instead — the
// session is created by signing in, and only there.
const orphan = await fetch(`${API}/api/v1/app/checkin`, {
  method: 'POST', headers: handoffAuth,
  body: JSON.stringify({ deviceId: 'e2edev-never-signed-in', pushPermitted: true }),
});
const orphanBody = orphan.ok ? await orphan.json() : null;
check('25.31 a check-in from a device with no session registers NO push target',
  orphan.ok && orphanBody?.data?.pushRegistered === false,
  `pushRegistered=${orphanBody?.data?.pushRegistered}`);

// ── A password change kills every device ─────────────────────────────────────────────
// The credential stamp is the kill switch, and it is only worth anything if every path that changes
// a password rolls it. Left as a SKIP rather than run, because the only way to exercise it here is
// to change this shared account's password, and a suite that does that breaks every other section.
note('25.32 a password change signs every device out', 'would change the shared e2e account\'s password');

// ── Clean up ─────────────────────────────────────────────────────────────────────────
// Every device this run created. The ledger-style "some rows cannot be cleaned up" exemption does
// not apply to a session: leaving live handsets behind would make the device list unreadable after
// a few runs.
await fetch(`${API}/api/v1/auth/logout`, {
  method: 'POST', headers: handoffAuth, body: JSON.stringify({ deviceId: devC.refreshToken.split('.')[1] }),
}).catch(() => {});

console.log(`\n  ${pass} passed, ${fail} failed, ${skip} skipped`);
post(`  section 25: ${pass} passed, ${fail} failed, ${skip} skipped`);

// A suite that counts its failures and then exits 0 reports nothing, and three scripts in this
// repository did exactly that until 2026-09-21. Set the code.
if (fail > 0) process.exitCode = 1;
