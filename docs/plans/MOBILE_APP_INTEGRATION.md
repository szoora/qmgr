# Mobile app integration — rebranding the MAUI shell for Q-Mgr

**Status:** BUILT 2026-09-22 (`66a03db`, mobile repo `1d238a1`/`7d7600c`) — every stage but iOS submission, which needs a Mac and an
Apple account. Push configured 2026-09-24. Written 2026-09-22 with the user's seven decisions (§11). (Header corrected 2026-09-25.)
**Mobile repo:** `D:\QMGR\Mobile\CashBook` (a working copy of `github.com/szoora/CashBookPro.git`)
**Reference server implementation:** `E:\ERP` (`sacc/Api/V1/*`, `sacc/Controllers/GetAppController.cs`)
**Contract:** `D:\QMGR\Mobile\CashBook\docs\product-onboarding.md` + `docs/onboarding-kit/`

The shell is a native MAUI host for a product's existing web UI that adds what a browser
cannot do: ESC/POS thermal printing over Wi-Fi and Bluetooth, a session that survives a
restart without storing a password, multi-workspace switching, FCM push, and out-of-store
self-update. It is ~7,900 lines of C# plus a 207-line JS bridge, and it is documented to a
standard this repository would recognise. **None of it should be rewritten.**

---

## 0. What was checked

**Two corrections to an earlier draft of this plan, both mine.** They are kept rather than
quietly removed, because each one changes an estimate.

- **The production build works and has shipped — I asserted a risk without checking.** An
  earlier draft made "preserve the CashBook work" a blocking first step on the strength of a
  dirty `git status`. Checked: `deploy/app-host/releases/cashbook/cashbook-1.3-3506724.apk`
  is a real 13.65 MB signed artefact with its sidecar (sha256, size, `versionCode 3506724`),
  `docs/releases/` holds **seven** further release entries, and
  `https://apps.cashbook.ug/cashbook/releases.json` **answers live** with `versionCode
  3503910`. `apps.json` lists one app, so adding Q-Mgr is one more folder. The build,
  signing, sidecar, manifest-regeneration and publish chain all work today.
  **Not a blocker, and not a phase.** The one fact worth one line: the *source* is on this
  machine and one commit, so a routine push is worth doing at some point for its own sake.
- **Q-Mgr's mark already exists, as vectors.** An earlier draft said no Q-Mgr artwork existed
  in either repository and that the user would need to supply one. Wrong — see §A3. This is
  the correction that saves the most work.

**And two things in the brief that sound settled and are not:**

1. **The ERP has moved past its own documentation.** `product-onboarding.md` says each product
   serves its own APK from `App_Data/app-releases`. `AppUpdateController.cs` says the opposite
   and is newer: *"This server no longer stores or serves the artefact. Every app is published
   once to the central distribution host."* That host is `https://apps.cashbook.ug/<app>`, laid
   out one folder per app, with `scripts/build-app-host.ps1 -App <name>`. So the user's
   *"the same processes for uploading the production archive will be used"* is exactly right,
   and Q-Mgr's work here is **one config value**, not a download subsystem.
2. **"The qz shim works with no web-side change" is true for the ERP and FALSE for Q-Mgr.**
   See §4.5 — a real bug, found by reading both sides, that would print garbage.
3. **Q-Mgr already has a PWA** — `manifest.json`, `service-worker.js`, a per-host
   `ManifestController`, and `pwa.js` with its own update banner. The native app does not
   arrive on empty ground; it arrives on top of a competing update mechanism and a competing
   asset cache. See §4.4.

---

## 1. The shaping decision: adapt the SERVER, fork the APP for branding only

Two ways to make the shell speak to Q-Mgr.

**A — Server-side adapter (recommended).** Q-Mgr grows a `MobileShell` surface that exposes
the shell's exact wire (routes, JSON field names, status codes) and delegates to the services
it already has. The app changes only in name, colour and icon.

**B — Change the app to speak Q-Mgr's native wire** (bare DTOs, `email`, `ExpiresIn`, 204 on
logout).

**Take A.** Three reasons, all evidence rather than preference:

- **The shell's own E2E suite is the conformance test.** `tests/auth_e2e.py` is 37 checks over
  rotation, replay revocation, the handoff's single-use code, the open-redirect guard and the
  credential-version kill switch. It is written against that wire. Option A keeps it usable
  against Q-Mgr on day one; option B throws away the only ready-made proof that any of this
  works and leaves this project writing it again.
- **Evolution already proved the shape.** Same problem, an architecture *"nothing like the
  ERP's"*, solved as a thin adapter delegating to existing Mediator commands: *"1 new folder
  plus 6 added lines, zero deletions."*
- **Option B's edits land in the files that are hardest to verify from here** —
  `AuthService.cs`, `TenantResolver.cs`, `UpdateService.cs` — none of which can be exercised
  without a handset, which is precisely this project's standing rule about what counts as
  verification.

**What A does not excuse.** Two things must change in the shell regardless, because they are
the shell being wrong rather than Q-Mgr being different: the base64 `qz.print` gap (§4.5) and
`Workspace.StorageKey` being the host alone (§4.1).

---

## 2. Phase A — rebrand and rename the app

### A0. Repo identity (not a blocker — see §0)

The Q-Mgr app is a fork, and its parent's history belongs to another product, so **a fresh git
history** (`git init`, one initial commit) is the honest shape; the provenance goes in the
README rather than in a shared `origin`. Copy rather than rename in place, so
`D:\QMGR\Mobile\CashBook` stays exactly as it is and keeps building CashBook.

### A1. Project identity

| Thing | Now | Q-Mgr |
|---|---|---|
| Folder | `D:\QMGR\Mobile\CashBook` | `D:\QMGR\Mobile\QMgr` (a copy — leave CashBook's alone) |
| Project / solution | `CashBook.csproj` / `.sln` | `QMgr.Mobile.csproj` / `QMgr.Mobile.sln` |
| Root namespace | `CashBook` | `QMgr.Mobile` |
| Android FCM service | `CashBookFirebaseMessagingService` | `QMgrFirebaseMessagingService` |
| Keystore env vars | `CASHBOOK_KEYSTORE*` | `QMGR_KEYSTORE*` |
| JS shim marker | `__cashbookProShim`, `cashbook:ready` | `__qmgrShim`, `qmgr:ready` |
| Distribution app slug | `cashbook` | `qmgr` |

244 occurrences of "CashBook" across 74 files. A namespace rename is mechanical; **the ones
that are not just names** are listed in A3 and §4.

**What must NOT be renamed.** The device-side storage keys are already brand-neutral —
`session.v1.`, `session.index.v1`, `workspaces.v1`, `workspace.current.v1`, `push.token.v1` —
so they carry none of the orphaned-session risk `product-onboarding.md` warns about at length.
Leave them exactly as they are. There is no installed Q-Mgr base to migrate, and renaming a
persisted key buys nothing and risks everything.

### A2. `brands/qmgr.json`

The brand system already exists and is verified (`-p:Brand=clinicbooks` was proven to change
the generated `package=` in `AndroidManifest.xml`). This is a JSON file and one command.

```json
{
  "id": "qmgr",
  "applicationId": "ug.qmgr",
  "title": "Q-Mgr",
  "urlScheme": "qmgr",
  "saasRootDomain": "cashbook.ug",
  "suiteName": "SACC",
  "webViewToken": "QMgrApp",
  "colors": {
    "primary": "#8c2f52", "primaryHover": "#a03a60", "primaryDark": "#6d2440",
    "accent": "#7a2847", "accentBright": "#a03a60", "ground": "#6d2440"
  }
}
```

- **Colour comes from `qm-theme.css`, not from a fresh choice.** `#8c2f52` dark / `#7a2847`
  light is the standing wine decision (2026-08-19) and CLAUDE.md is explicit that changing it
  is the user's call, not a side effect. The derived shades follow `BrandPalette`'s own
  arithmetic — mix toward black for `-dark` — so the app and the web agree by construction
  rather than by two people picking similar hexes.
- **`webViewToken` is a contract with the site, not a label.** `Brand.g.cs` says so, and names
  the bug: the Fleet site matches the literal `TCYFleetApp`, and a token derived from the title
  would silently stop matching. If Q-Mgr's web is ever to detect the app, it matches `QMgrApp`.
- **`applicationId` is `ug.qmgr`** (user decision, 2026-09-22). It validates as lowercase
  reverse-DNS with two dots, and it is permanent the day anything is uploaded to Play — which,
  per decision 2, is not now, but the id still cannot change once handsets carry it: on Android
  the package name *is* the app's identity, so changing it later is a new app and a manual
  reinstall on every device.
- **A new signing keystore is required either way.** `D:\QMGR\Mobile\Keys\cashbook.p12` is
  CashBook's. Out of store there is no Play App Signing to fall back on, so **the Q-Mgr
  keystore is the only thing that can ever sign an update for an installed device**: Android
  refuses an update signed by a different key, and the only remedy is uninstall-and-reinstall,
  which erases everything on the device. Back it up somewhere that is not this machine, as its
  own task.

### A3. Artwork — the mark already exists, and no glyph extraction is needed

**Correcting §0:** Q-Mgr's mark is already drawn, already wine, and already vector, in
`src/Q-Mgr.Web/wwwroot/`:

| File | What it is |
|---|---|
| `images/icon-512.svg` | The mark on a `#8c2f52` plate, `rx="96"` |
| `images/icon-512-light.svg` | Same geometry, wine on white |
| `images/icon-adaptive.svg` | A 48×48 variant with `fill="currentColor"` on the plate |
| `images/logo.svg` | Loose logomark, no plate |
| `favicon.svg` | 32×32 |

It is a **queue loop resolving into a forward arrow** — a stroked circle plus a rotated
rectangle and triangle — and its own comment says why: *"the ring is people cycling through
service, the arrow is 'next'."* That is eight lines of SVG primitives.

**So `build-brand-assets.py`'s whole extraction pipeline is bypassed for Q-Mgr.** It exists
because CashBook had no standalone glyph and one had to be recovered from a lockup by connected
components at a hand-found offset — `docs/branding.md` records two approaches that produced
plausible, wrong output. None of that applies: Q-Mgr's glyph *is* standalone, *is* parametric,
and renders at any size from the same numbers. The Q-Mgr path emits the assets directly.

**Three things to fix on the way through, all found by reading the files:**

- **`logo.svg` is still the OLD BLUE `#0058cc`.** Every other mark is `#8c2f52`. That blue is
  the pre-2026-08-19 brand colour, and CLAUDE.md is unambiguous: *"This is now the deliberate
  colour decision — do not revert to blue."* The file's own comment says it is *"for use on
  marketing/docs surfaces"*, which is the worst place for it to be off-brand. **This is a bug in
  the web repo, not in the app**, and it is a one-line fix worth reporting separately.
- **`icon-adaptive.svg` cannot be used as an adaptive icon as it stands.** MAUI's
  `<MauiIcon Include="…" ForegroundFile="…" Color="…" />` wants the plate and the glyph as
  **two layers**; this file has both in one square, with a `rx="12"` corner radius **baked in**.
  A launcher applies its own mask, so a baked radius yields a rounded square inside a circle.
  The glyph also runs close to the edge — its arrow tip reaches 83.6% of the half-width, against
  an adaptive-icon safe zone that is a circle over the central 72 of 108dp — so **it must be
  measured, not assumed**. That is exactly what `artwork/verify-brand-assets.py` is for: the
  safe zone is measured there, and an earlier CashBook revision sat *exactly* on the 313px
  boundary and was shaved by some launchers. Split the layers, inset the glyph, and require
  **14/14**.
- **`Resources/Images/brandmark.png` must not silently stay CashBook's.** A wrong logo on a
  sign-in screen reads as the wrong app, not as a missing asset. It is regenerated from the same
  geometry — glyph only, no plate, since the page renders the wordmark as live text.

**The SACC parent mark, for the About screen and the suite line**, is at
`D:\SACC_SOFTWARE\ART WORK\Logo\` (`sacc.png`, `logo_teal.png`, `logo_yellow.png`) and
`…\Icon\` (`SACC.ico`, `Icons/`, `Icons_Full/`). SACC teal is `#2e6b62`, already recorded in
`docs/branding.md`. `Brand.SuiteName` stays `"SACC"`: Q-Mgr is a SACC product and the app should
say so where a user looks for provenance, without the suite mark competing with Q-Mgr's own on
the launcher or the sign-in screen.

### A4. Store and legal text

`docs/store-listing.md`, `docs/privacy-policy.md`, `docs/site/privacy.html`, `docs/README.md`,
`README.md` are all about an accounting companion with a cash drawer. They are rewritten from
what the Q-Mgr app actually does, not edited. §6 covers what the privacy notice must now say,
which is materially different: this app reaches child safeguarding records.

---

## 3. Phase B — the server side, in Q-Mgr

Everything lands in the API as one folder, `src/Q-Mgr.API/Controllers/v1/MobileShell/`, plus one
Blazor page and one login-page link in Web. Q-Mgr's nginx is already single-host path-based
(`/` → Web, `/api/` → API) **and the generated per-tenant custom-domain blocks carry `/api/`
too**, so the app reaches one origin for both halves and no `webUrl`/`apiUrl` split is needed.
That is the Evolution complication Q-Mgr does not have.

| # | Endpoint | Q-Mgr today | Work |
|---|---|---|---|
| 1 | `GET api/v1/tenant/info` | — | New. §3.1 |
| 2 | `POST api/v1/auth/login` | exists, different wire | Adapter |
| 3 | `POST api/v1/auth/refresh` | exists, **one plaintext token per user** | §3.2 — the real work |
| 4 | `POST api/v1/auth/logout` | exists, no device, 204 | Adapter + 400 |
| 5 | `POST api/v1/auth/web-handoff` | — | §3.3 |
| 6 | `GET api/v1/auth/web-session` | — | §3.3 — **not a cookie here** |
| 7 | `GET api/v1/app/update` | — | Read-through to the distribution host |
| 8 | `GET api/v1/app/releases` | — | Same |
| 9 | `GET branding/product-logo` | org logo exists | Thin, honour `?ink=` |
| 10 | `POST api/v1/app/checkin` | — | §5.1 (push) |
| 11 | `GET api/v1/app/broadcasts` | Q-Mgr has a real notification centre | §5.1 — map, don't port |
| — | `/getapp` page + login link | — | §3.4 |

### 3.1 `tenant/info` — and the thing that is genuinely different

Flat (not wrapped), anonymous, **503 when the host answers but is not a workspace**. It is the
one call made against an unknown host, before a password is typed.

Q-Mgr's answer depends on which host it is, and `TenantHostContext` already knows:

- **A tenant's own domain** (`dashboard.maryhillug.net`): `IsTenantHost` is true only for a
  **live, verified** domain, so the answer carries the school's name, its `LogoUrl`, its brand
  colours and its organization id. This is exactly the ERP's case.
- **The shared platform host** (`qmgr.cashbook.ug`): there is no tenant. It must answer with
  the **platform** identity — `companyName: "Q-Mgr"`, `product: "Q-Mgr"` — and never invent a
  school. `IsTenantHost` being false for a half-configured host is load-bearing here: a
  mistyped DNS record must not half-brand a sign-in page.

**Do not return 503 on the shared host.** It is a legitimate workspace for every tenant that
has not bought a domain — which is most of them. 503 is for a host that is not a Q-Mgr install.

Colours come from `BrandPalette`'s inputs, and `api.*` advertises `refresh`, `webSession` and
`deviceSessions` so the app detects an older deployment without probing for 404s.

### 3.2 Per-device sessions — the one piece that is real engineering

Q-Mgr today: `User.RefreshToken` is a **single nullable plaintext string column**, one per
user, matched by an unindexed equality scan, with a 7-day expiry and no rotation semantics
beyond replacement. Consequences, in plain terms:

- **A phone signing in evicts the browser**, and the browser refreshing evicts the phone.
  CLAUDE.md already records the same shape from the other side: *"`auth/logout` revokes the
  user's one refresh token, which after a sign-in on another device is that device's live
  session."*
- There is nothing to revoke **one** lost handset with.
- A database copy yields a usable credential.

The contract requires four properties, and RFC 9700 §4.14 requires the second of them outright
for public clients (a mobile app is a public client): store only a **hash**; **rotate on every
redeem** so a stolen token works at most once and its reuse is *detectable*; **bind to the
credential version** so a password change kills every device; and make the token **carry its own
identity** (`{userId}.{deviceId}.{secret}`) so validation is one keyed lookup.

**This is the one place a new table is right**, and the standing enhance-before-add constraint
is satisfied on its own terms — *"a sub-resource with an independent lifecycle an existing row
cannot represent no matter how many columns it grows"*, the `WelfareAttachment` clause. One
column cannot hold N devices.

- `UserDeviceSession` — `UserId`, `DeviceId`, `DeviceName`, `TokenHash`, `CreatedAt`,
  `LastUsedAt`, `ExpiresAt`, `RevokedAt`, `CredentialStamp`. Unique on `(UserId, DeviceId)`.
- **The credential version is a cheap nullable column on `Users`**, not a second table: Q-Mgr
  is not on ASP.NET Identity and has no security stamp. Rotate it wherever the password
  changes — and the sweep for *every* such place is part of the work, because one missed writer
  is a password change that does not kill a device.
- **`User.RefreshToken` stays, unchanged, for the browser.** Migrating the web onto the new
  table is a separate decision with a blast radius across every signed-in tab, and folding it
  into the mobile change is how one feature takes out another.
- Redeeming runs under `pg_advisory_xact_lock` on the session row — this codebase's own idiom
  for "exactly once" — because two concurrent refreshes from one handset must not both rotate.

### 3.3 The web handoff — architecturally different, and this is the part to read twice

The contract assumes the web UI is **cookie**-authenticated: `web-handoff` mints a code,
`web-session` is *navigated to*, sets the cookie, redirects.

**Q-Mgr's web UI is Blazor Server and its session lives in `localStorage`** —
`access_token`, `refresh_token`, `user_info`, read by `AuthService` through
`ILocalStorageService`. There is no cookie to set. So `web-session` cannot be an API redirect
that authenticates the browser; it has to be a **Web-side page** that redeems the code
server-to-server, writes those three keys, and then navigates.

Shape:

1. `POST api/v1/auth/web-handoff` (bearer) → a single-use code, 60s, on the API.
2. The app navigates the WebView to `https://<host>/mobile-session?code=…&returnUrl=%2F`.
3. That Blazor page calls the API **server-to-server** to redeem the code, receives the same
   `LoginResponse` the login endpoint returns, and hands it to the existing
   `AuthService`/`AppInitializationService` path so the session is stored exactly the way a
   normal sign-in stores it. One writer, no second copy of the storage rule.
4. Then `NavigateTo(returnUrl)`.

Four rules carried over verbatim, each of which cost the ERP real time:

- **The code is consumed BEFORE the sign-in**, so a failure downstream cannot leave a
  replayable code.
- **`returnUrl` is validated as local.** An anonymous endpoint taking a redirect target is an
  open redirector otherwise.
- **Every failure path redirects; none returns JSON.** This page is *navigated to* — a
  `{"success":false}` body renders as raw text inside the app and looks exactly like a broken
  app. It happened on the ERP.
- **The page must not pass through `AuthorizeRouteView`.** CLAUDE.md's share-page bounce is the
  same class: an anonymous visitor's auth state is never ready synchronously, so a public page
  spends its first render inside `MainLayout`, whose own check redirects to `/login`. `Routes.razor`
  already sends a page with no `[Authorize]` through a plain `RouteView` — keep it that way, and
  reproduce the class by restarting the Web process under an open tab, never by fresh navigation.

### 3.4 `/getapp` and the login-page link

`AppUpdateController` and `GetAppController` are read-through: `Mobile:DistributionUrl` =
`https://apps.cashbook.ug/qmgr`, cached 5 minutes with a last-good fallback, anonymous, and
comparing on the **integer** `versionCode` (`"1.10.0"` sorts before `"1.9.0"` as text, and that
mistake ships an update every device then refuses). `mandatory` is **sticky across versions**,
and `available` reflects the disk rather than the manifest, so a pruned build is reported as
unavailable rather than offered as a link that 404s.

**`get:/api/v1/app/*` must be whitelisted from IP rate limiting in code**, the same
`PostConfigure<IpRateLimitOptions>` as `/uploads/*` and `/api/v1/health`, and for the same
reason: the DB "RateLimiting" row replaces the config section wholesale, and an
administrator's edit must not be able to stop a mandatory security update reaching handsets.

The page is a Blazor page on `PublicLayout`, anonymous, and needs **no new dependency**: Q-Mgr
already loads `qrcodejs` from CDN for visitor badges, which is what draws the provisioning QR
(`qmgr://workspace?host=…&name=…`). It states the workspace address with a copy button, lists
only downloads whose files exist, shows earlier builds **collapsed with the warning before the
links** (Android will not install an older version over a newer one — the app must be
uninstalled first, which erases everything on the device), and explains an empty state rather
than rendering a blank panel.

**Two Q-Mgr-specific wordings.** Say *"no builds published on this server"*, never *"for this
workspace"* — one app serves every tenant, and the other phrasing sends an administrator
hunting for a per-tenant upload that does not exist. And say **"Email or staff username"**
everywhere the app or the page names the sign-in field: 133 of the 184 staff on the first real
school list have no email address at all, and `User.Email` is nullable for exactly that reason.

**The link goes on `/login`**, which is where the ERP puts it and for the reason its own comment
gives: *"A page nobody links to is a page nobody finds."* Q-Mgr's login footer already has an
`auth-doors` row (Join Using Link / Register Organisation). **The app link must not be a third
`auth-door`** — those two buttons answer "how do I get in", and 2026-09-20's fix was precisely
about not conflating two different doors. A separate quiet pill above `footer-links`, as the ERP
has it, keeps that distinction intact. `route-audit.mjs` must be run after, since it reads every
`@page` and every `href`.

---

## 4. The architecture differences, stated plainly

These are the things that are different about Q-Mgr, each with what it costs.

### 4.1 Three ways a school is addressed — and the ERP owns the wildcard

`saasRootDomain` is `cashbook.ug` and full white-label domains are supported (user decision,
2026-09-22). That gives three shapes, and **the middle one needs provisioning that the first and
third do not**:

| Typed | Opens | Needs |
|---|---|---|
| `qmgr` | `https://qmgr.cashbook.ug` | nothing — live today |
| `maryhill` | `https://maryhill.cashbook.ug` | a DNS record **and** an exact nginx block |
| `dashboard.maryhillug.net` | itself | the existing white-label path, its own certificate |

**Probed, not assumed** (2026-09-22): `qmgr.cashbook.ug/api-health` answers **200 Healthy**, so
the app's `/api/v1/…` calls reach the API through nginx on the production host.
`lpos.cashbook.ug` answers **302** — an ERP tenant. And `notarealtenant9z.cashbook.ug`
**does not resolve at all**, so there is **no wildcard DNS** on the zone: every subdomain is an
explicit record.

**The ERP holds the wildcard server block**: `server_name erp.cashbook.ug *.cashbook.ug;` in
`scripts/deploy/deploy/saas/config/erp.conf`. So for a Q-Mgr school on
`maryhill.cashbook.ug`, three consequences follow, and the third is the dangerous one:

- **An exact `server_name` beats a leading wildcard**, so a provisioned Q-Mgr block wins. The
  ERP's own install script says as much in a comment. This is the mechanism, and it works.
- **The wildcard certificate already covers `*.cashbook.ug`**, so a Q-Mgr subdomain tenant is
  the **fast path** of the tenant-domain activator built on 2026-09-21: nothing is issued,
  nothing renews, nothing can be rate-limited by Let's Encrypt. One nginx block and a reload.
  That is a genuinely cheap path and it already exists — `ICustomDomainService` plus
  `qmgr-tenant-domain` through the spool, never sudo.
- **Without a block, the school's typed code lands on the ERP, silently.** Not an error — the
  *wrong product*. The app would probe `/api/v1/tenant/info` and get the ERP's answer or a
  redirect, and the user would see another company's name or an unexplained refusal. This is
  precisely the hazard Q-Mgr's own CLAUDE.md names from the other direction: *"a wildcard on a
  box shared with ERP, CashBook and the rest would quietly catch hostnames belonging to somebody
  else."*

**Two operational rules fall out, and neither has any code to enforce it:**

1. **The `cashbook.ug` subdomain namespace is shared between two products on one box.** A Q-Mgr
   school cannot take a name an ERP tenant already uses, and vice versa. Somewhere there needs to
   be one list. Until there is, provisioning a Q-Mgr subdomain means checking the ERP's tenants
   first.
2. **A bare code must not be offered as if it always works.** The app should treat an
   unprovisioned `<code>.cashbook.ug` the way it treats any host that is not a workspace — the
   probe already exists, and `tenant/info` returning something that is not Q-Mgr's must read as
   *"no Q-Mgr workspace called maryhill"*, never as a login failure. That is the whole point of
   probing before a password is typed.

### 4.1b A workspace is keyed by host AND school (decision 7)

`Workspace.StorageKey => Host.Replace(':','_').Replace('.','_')`. On the ERP that is correct:
*"the server resolves tenancy from the request host, so the workspace address IS the tenant
selection."* On `qmgr.cashbook.ug` it is not — every school that has not bought a domain shares
that host and the tenant comes from the signed-in user.

Left alone, two schools collapse into **one** workspace entry: one row in the Recent list, one
stored session slot. **Keyed per host and school** (user decision, 2026-09-22) they do not, and
the field already exists — `Workspace.Tenant`, added for Evolution — so `StorageKey` becomes host
plus tenant.

Two consequences of that decision to build deliberately rather than discover:

- **The school is not known until after sign-in on the shared host.** `tenant/info` there
  answers for the platform, so the workspace row is created with no tenant and **adopts** the
  organization from the login response. So the key must be allowed to change once, and the
  session must be stored under the final key — writing it under the tenant-less key first and
  re-keying later is how a session goes missing after the first restart.
- **The switch must RELOAD the web view and clear its storage**, which matters *more* here than
  on the ERP, not less: two Q-Mgr schools share an origin, so the WebView's `localStorage` —
  which is where Q-Mgr's session actually lives — is the same jar for both. Track *which* host
  and school is loaded, not *whether* anything is. `product-onboarding.md` names this trap
  explicitly, and on a shared origin it is the difference between a switch and a data leak.

### 4.2 Rate limiting keys on the IP, and a school is one IP

`IpRateLimiting` keys on `X-Real-IP`, which nginx sets from the connecting address on public
paths. Forty handsets on one school's Wi-Fi are **one public address**, so the whole school
shares one bucket — the identical failure the first load test found from the other direction,
where every signed-in person shared the Web server's loopback and 50 users produced 97% 429s.

The web half was fixed by relaying the viewer's address. **The mobile half cannot be fixed that
way**: the address really is shared. So authenticated mobile traffic needs a key that is not
the address — the user id, or the device id the app already sends. This is a real change, and
it is cheaper to make before a school is on it than after.

### 4.3 `identify` enumerates accounts

`POST api/v1/auth/identify` is anonymous and answers *"No account found with this email or
username"*, returning the organization **name** for any valid identifier when no tenant is
resolved. The contract requires the opposite of `login` — *"one message for wrong-password and
unknown-user so it cannot enumerate"* — and the registration guard's own design notes warn
about enumeration elsewhere. Pre-existing, not introduced here; but the app leans on identify-
style probing by design, so it belongs on the list rather than in a footnote.

### 4.4 The PWA and the native app will fight unless told not to

Q-Mgr ships a service worker that precaches assets and `pwa.js`, which shows its own **"Update
Now"** banner. Inside the native shell that is two update mechanisms on one screen: the app's
mandatory-update dialog, and a web banner offering to reload a page the app is hosting. The
`pwa.js` comment already records that a spurious reload on the login page read to users as a
login bug — the same reload inside a WebView reads as the app crashing.

Also: `WebViewSession.ClearAsync` deletes **all** site data on sign-out and on workspace
switch, service-worker cache included, so every switch is a full asset re-download over the
school's connection.

**Recommendation:** suppress the PWA's install prompt and update banner when the User-Agent
carries `QMgrApp`, and let the native updater own updating. One concept, one owner — the rule
this codebase applies to everything else.

### 4.5 The `qz` shim does not decode base64 — and Q-Mgr sends base64

This is a real bug, found by reading both sides, and it is the difference between "printing
works" and "printing emits a page of gibberish".

`src/Q-Mgr.Web/wwwroot/js/print-service.js` calls:

```js
const rawData = [{ type: 'raw', format: 'base64', data: escPosDataBase64 }];
await qz.print(config, rawData);
```

`Resources/Raw/bridge.js` handles `p.data` only as a string and concatenates it, forwarding the
**base64 text** to the printer as though it were ESC/POS bytes. Nothing errors; the printer
receives `G1Ah...` and prints it.

**The fix belongs in the shim, not in Q-Mgr's web code**, because QZ Tray's real API supports
`format: 'base64' | 'hex' | 'plain'` and the shim is simply incomplete against it. Honour
`format`, decode base64 to bytes, and **reject an unknown format loudly** rather than passing it
through — the shim's own header already states that principle: *"Anything else is deliberately
absent so a missing feature fails loudly here rather than appearing to work and printing
nothing."*

That Q-Mgr already calls `qz.*` at all is the strongest argument for this app existing: the
kiosk's ticket printing and visitor badges reach a Bluetooth or Wi-Fi thermal printer **with no
change to Q-Mgr's web code**, which is exactly what the shim was built to promise.

### 4.6 Per-tenant Firebase credentials cannot work

`NotificationSettings` carries `FirebaseProjectId`, `FirebasePrivateKey`, `FirebaseClientEmail`
**per tenant**. One installed app ships one `google-services.json` and therefore one FCM
sender; a tenant's own Firebase project cannot deliver to a binary registered with a different
one. Those three columns are unusable for this purpose.

**Push credentials belong at the platform level**, and this codebase already has the precedent
and the shape: `PlatformEmailDefaults` fills the platform Email row from configuration **only
when it is blank**, after the RBAC seeder, so an existing install picks the account up and an
administrator's own choice is never overwritten. Do the same for FCM. The secret goes in the
**systemd unit** as `Push__ServerKey`, never in `appsettings.Production.json` — `install.sh`
preserves the API's copy of that file on every upgrade, so a key added there never reaches a
live server. That is the same trap three times over already: `MediaStorage__PublicBaseUrl`,
`Email__*`, `DataProtection__KeyPath`.

### 4.7 Signed upload links die in an hour

Gated uploads are served for a one-hour `?t=` token minted at the point a DTO reaches a caller.
Inside the app, a page left open past an hour shows broken images for every student photograph
and welfare attachment — the same as in a browser, but a tablet at a front desk stays open far
longer than a browser tab. Worth measuring before deciding whether it needs anything; the
existing `onerror="this.remove()"` fallback on `QAvatar` already degrades to initials, which
suggests the pattern to follow rather than a new mechanism.

---

## 5. Functional recommendations

Scoped to the mobile integration. Ordered by what a school would actually feel.

### 5.1 Push notifications — the reason this app is worth more to Q-Mgr than to the ERP

CLAUDE.md, 2026-09-17: *"There is no push sender... `NotificationChannel.Push` and the
Firebase/PushSent columns stay in the schema **for the day one does**."* That day is this one.
This is the single largest functional gain, and almost all of the work is already built on both
sides — `NotificationDispatchJob` on Hangfire/PostgreSQL, `NotificationLog` with its
skipped-vs-failed discipline, `NotificationEventKeys` as a persisted wire format,
`Notification.ActionUrl`, and `PushService` + `NotificationInbox` in the app.

- Add `Push` as a third channel in `NotificationDispatchJob`, returning
  `ChannelSendResult.Sent/Skipped/Failed` like the other two. **A device with no token is
  Skipped with a reason, never `Success = false`** — that is the delivery log's standing rule.
- `POST api/v1/app/checkin` stores the FCM token per `(User, DeviceId)` — the same row
  `UserDeviceSession` already needs, not a second table.
- **The inbox reads Q-Mgr's real notifications, not a ported `broadcasts` feed.**
  `GET api/v1/notifications` already takes `eventKey` and `offset` and already carries
  `ActionUrl` and `ReadAt`. Porting the ERP's broadcast endpoints would create a second
  notification concept in a product that has a good one.
- A push is a **doorbell**; the list is the source of truth. `NotificationInbox` is already
  written that way.
- The events worth carrying: a welfare alert, the duty/register chase, a minutes action point
  falling due, a visitor arrival for the person being visited, a document share opened. **Not**
  every row — CLAUDE.md's own note that eleven identical "Recognition logged about you" rows
  buried the appraisal is the warning here.
- **Restricted and Confidential rungs must be honoured in the notification body.** A lock-screen
  preview is read by whoever is holding the phone. The rule already exists: *"a Confidential
  record tells its subject that it exists and what it is called"* — the content is read in the
  app, behind the session. A push that quotes a safeguarding note on a lock screen would break
  that in the most public place available.

### 5.2 Deep links, so a notification opens the right page

`ActionUrl` is already on every notification, and 96 of the last 100 carry one. Register the
`qmgr://` scheme plus **Android App Links** on the tenant host, so a notification tap opens the
app on that page rather than the dashboard. The shell already has the deep-link plumbing for
`cashbook://workspace?host=…&name=…`.

### 5.3 Kiosk and front-desk mode — the strongest device-level case

Q-Mgr's kiosk is a first-class screen with its own layout, and a school's front desk is a
tablet. The native shell can do what a browser cannot: **Android screen pinning / lock task**
so a visitor cannot leave the kiosk, keep-awake, and thermal ticket and badge printing through
§4.5's fixed shim. That combination is the clearest answer to "why not just the PWA", and on
iOS it is also the answer to App Store guideline 4.2 (see §6.2).

### 5.4 Native QR scanning

Q-Mgr scans visitor badges with `jsQR` in the browser via `getUserMedia`. The app already holds
`CAMERA` permission. A native decode is faster, works at a worse angle, and sidesteps the
WebView camera-permission prompt entirely. Route it through the existing bridge.

### 5.5 Offline — say what it is, do not half-build it

Full offline for a Blazor **Server** app is not a feature, it is a different product: the UI is
rendered on the server over a SignalR circuit, so no circuit means no UI. Two honest options:

- **State it.** A clear "no connection" screen with a retry, and nothing pretending to work.
  `ConnectivityWatcher` already exists for this.
- **Queue exactly one path**, if the user wants it: taking a register where a staff meeting has
  no signal. That is a native form writing to a local queue and replaying it — real work, worth
  doing only if the schools ask.

**Do not** ship a cache that makes stale safeguarding data look current. Given the record this
product keeps, that is the worst available failure.

### 5.6 Biometric unlock

A session that reaches welfare records should re-authenticate on resume, not just on install.
Platform biometrics with a PIN fallback, and a policy the tenant sets. This is table stakes for
comparable products holding sensitive records, and MASVS-AUTH treats session lifecycle and
automatic logout as a requirement rather than a nicety.

---

## 6. Compliance recommendations

### 6.1 Distribution: out of store, decided — and it does not mean the same thing on all three platforms

**Out of store for now (user decision, 2026-09-22.)** That is also the shape the ERP already
runs, and it removes the conflict the manifest had set up: `REQUEST_INSTALL_PACKAGES` is how the
app installs its own updates, and Play's policy on that permission says it *"may not be used to
perform self updates, modifications, or the bundling of other APKs... unless for device
management purposes."* Off Play, that policy does not apply.

**But "out of store" only exists on two of the three platforms**, and with all three in scope
(decision 6) that asymmetry has to be planned for rather than met later:

| | Android | Windows | iOS |
|---|---|---|---|
| Out-of-store install | yes, APK | yes — `WindowsPackageType=None`, so an unpackaged app | **no** |
| Self-update | yes, and built | no — reports only | **no**, and cannot be |
| Review needed | none | none | **yes, always** |

`UpdateService` already gets this right — only the Android branch installs, and every other
platform *"installs through its own channel"*. The consequence for iOS is the one that bites:
**Apple permits no self-distribution to customers**, so an iOS build reaches a school through
the App Store, TestFlight, or a Custom App in Apple Business Manager — and **all three go
through Apple review**. Private distribution is not an exemption; Apple states that Custom Apps
go through app review under the same guidelines. So choosing out-of-store does not get iOS out
of review; it only means there is no *Android* review. §6.2 is therefore a gate to pass, not a
risk to defer.

**Windows has a publishing gap, not a policy one.** `release-info.ps1` reads `versionCode` and
`versionName` out of an APK with `aapt dump badging`, and `build-app-host.ps1` stages `.apk` and
`.tar.gz`. **There is no path for a Windows or iOS artefact in either script.** For all three
platforms that is real work in the publish tooling — and it must keep the rule the existing
script enforces: read the version **out of the artefact**, never type it and never parse it from
a filename.

**But out-of-store now has its own deadline.** Google is extending developer verification to
sideloaded apps on certified devices: Brazil, Indonesia, Singapore and Thailand from
**30 September 2026**, and **globally through 2027**. Uganda is not in the first wave, so there is
runway — and there is an account type built for exactly this case: the **Android Developer
Console**, for developers *"distributing apps exclusively outside Google Play"*. Register there
before the global rollout, or every Q-Mgr install eventually fails on a certified device with no
error a school could interpret.

### 6.2 Platform requirements

- **Target API 36 — already met.** `net10.0-android36.0`. Play's deadline was 31 August 2026 and
  this build clears it.
- **16 KB memory page size.** .NET MAUI and .NET for Android ship 16 KB support, but the toolchain
  does not fix **third-party** native libraries, and this app carries two —
  `Xamarin.Firebase.Messaging` and `Xamarin.GooglePlayServices.Base`. Verify the `.so` alignment in
  the built `.aab`; do not assume it.
- **`RuntimeIdentifiers` is `android-arm64` only**, deliberately, to cut 12.5 MB from the download.
  That excludes 32-bit-only devices. For a school with older tablets that is a real limit and
  should be stated on the download page rather than surfacing as "it will not install".
- **iOS: guideline 4.2 is a gate, and all three platforms are in scope (decision 6).** A WebView
  wrapper that adds nothing gets rejected, and per §6.1 there is no route to an iPhone that skips
  review. So the native-value features are not a later phase on iOS — **they are the submission
  requirement**, and the order of work changes accordingly.

  What counts as native value there, and what is unavailable:

  | Native feature | iOS |
  |---|---|
  | Push notifications | yes — and the strongest single item |
  | Wi-Fi thermal printing (TCP :9100) | yes |
  | **Bluetooth thermal printing** | **impossible** — Apple permits BLE or MFi-certified accessories only, so a generic ESC/POS printer cannot be driven at all |
  | Device sessions, biometric unlock | yes |
  | Kiosk lock-down | only via MDM / Autonomous Single App Mode, never app-initiated |
  | Self-update | no |

  **Sequence iOS behind C2 and C3**, not because iOS is lower value but because submitting before
  push and printing exist is submitting the thing 4.2 rejects. Android and Windows can ship
  earlier precisely because nobody reviews them — which is a reason to order the work that way,
  not a reason to drop iOS.
- **The Apple Developer Program is a prerequisite with a lead time**, and `EnableAppleTargets`
  already gates the Apple target frameworks so a Windows box builds green without them — but
  **iOS and Mac Catalyst builds require macOS**. That is a machine, not a setting.

### 6.3 Data protection — this app is not the ERP's app

The ERP's mobile app touches invoices. This one touches a child's safeguarding chronology,
guardians, flags and photographs of injuries. That changes the posture, not just the paperwork.

- **Target audience is ADULTS, and must be declared as such.** Play's Families policies attach to
  apps whose target audience *includes* children — a staff app does not, and declaring otherwise
  would pull in the approved-SDK and identifier restrictions for no benefit. But the **Data safety**
  declaration must still honestly describe the personal and sensitive data the app processes,
  including data about third parties, and Play requires a privacy policy link both in Console and
  **in the app**.
- **The privacy notice is Q-Mgr's own and must be extended.** `/privacy` exists and already covers
  what schools keep. It must now also state what lives on the **device** (a revocable refresh
  token in the platform keystore; WebView cache and storage; an FCM token), the permissions and why
  each is needed, and that there are no third-party analytics SDKs — the CashBook notice can say
  that because it is true, and it is worth keeping true.
- **Account deletion.** Play requires an in-app route and a publicly reachable web route to request
  account and data deletion. Q-Mgr's tenant purge is a *tenant* lifecycle; a **user**-level
  deletion request path is a different thing and does not exist. It is also genuinely awkward here
  — a member of staff named in a safeguarding record cannot simply be erased, and the existing
  statutory-retention and de-identification machinery is the right place to answer it honestly
  rather than with a delete button.
- **Device loss is now a real event.** The device list plus revoke that §3.2 builds is the answer,
  and it should be reachable by a tenant administrator, not only by the person who lost the phone.
- **`FLAG_SECURE` on welfare surfaces.** Blocking screenshots and the recents thumbnail on screens
  showing safeguarding content is the mobile counterpart of the watermark rule the document viewer
  already follows — and, like that one, it is attribution and friction, not prevention. Say so.

### 6.4 MASVS, the short list that actually applies

A WebView with JavaScript enabled and a native bridge is the exact shape MASVS-PLATFORM warns
about. Four checks worth making explicitly:

- **The bridge surface is the attack surface.** It already exposes printing and the drawer.
  Nothing on it should be reachable from an origin the app did not navigate to itself — verify the
  origin check, and that `bridge.js` is injected only for the workspace host.
- `allowFileAccess` / `allowUniversalAccessFromFileURLs` off.
- **No token in any log.** MASTG calls this out because it is common; grep it rather than assume it.
- Every URL the WebView loads is validated against the resolved workspace, so a link in a
  broadcast cannot navigate the authenticated view somewhere else.

### 6.5 Accessibility

WCAG 2.2 sets 24×24 CSS px as the AA floor for target size and 44×44 for AAA. Q-Mgr's own
phone floor is already **40px** and is documented as *"a finger, not a pointer"* and deliberately
not scaled with the desktop tokens. The native shell's own controls have had no such audit —
`mobile-readiness.mjs`'s method (hit-test with `elementFromPoint`, and treat a fixed overlay
crossing flowed content as scrolling rather than overlap) is the standard to hold them to, and
its own first run is the warning: it reported 27 failures that were not real, and
**a measurement that flags the wrong thing is worse than no measurement, because work gets done
to satisfy it.**

---

## 7. UI/UX recommendations

### 7.1 Two navigation systems will stack, and that is the classic hybrid mistake

Q-Mgr shipped a **bottom navigation bar under 768px** today — four permission-chosen slots plus
Notifications and More, 56px plus the safe-area inset, reserved as `.qm-main` padding rather than
overlaid. The native shell has its own chrome. Inside the app, a phone would render the web bar
**and** the native shell's navigation.

Decide one owner. The web bar is the better one: it is permission- and module-aware
(`MobileNav.SlotsFor`), it already refuses to render a slot the caller cannot open, and it is
verified. So the native shell should stand back on a phone — and the mechanism already exists in
concept: the `QMgrApp` User-Agent token.

**Verify the safe-area inset resolves inside the WebView**, because `--qm-mobilenav-total` depends
on `env(safe-area-inset-bottom)` and a WebView does not always report what the browser does. That
is a measurement, not an assumption.

### 7.2 The sign-in screen has to answer a question the ERP's does not

The shell asks for a **workspace** first. On the ERP that is meaningful — every tenant has its own
address. For a Q-Mgr school without a custom domain the honest answer is "type `qmgr`", which reads
like a riddle.

- Default the field to the platform workspace and label the alternative plainly: *"My school has
  its own address"*.
- **Carry Q-Mgr's two doors into the app.** `/login` offers Join Using Link and Register
  Organisation, and 2026-09-20's fix was about naming them so a teacher does not create a duplicate
  school. A native sign-in screen with neither strands the teacher whose school sent them a join
  link. The app should reach the same `/join` flow in the WebView — and, on a tenant's own host,
  **hide Register** exactly as the web does, for exactly the same reason.
- **"Email or staff username"**, per §3.4.

### 7.3 Name things the way the product names them

`product` from `tenant/info` is what the app displays, and the app *"does not guess"*. Q-Mgr's
descriptor is **"Front Office"** — two words, the hyphen dropped and "Platform" dropped as
redundant, both decided 2026-09-22. The shell's own strings ("workspace", "company's books",
"till") are accounting language and must be rewritten: a school has **branches**, not tills.

### 7.4 White-labelling has to reach the native chrome

A tenant that bought `FeatureCodes.RemoveAttribution` gets Q-Mgr's name off six public pages, the
shell footer and email. If the native app's own header, splash and about screen still say Q-Mgr,
they have not got what they paid for on the surface they look at most. The runtime branding layer
already exists for this — `WorkspaceBranding` applies `brand.primary`/`accent` from `tenant/info`
and resets on sign-out — so `tenant/info` should also report whether attribution is removed, and
**never invent a colour**: if a workspace reports nothing, the app keeps its own palette, because
*"a half-applied theme reads as a rendering fault."*

### 7.5 One warning from the shell's own history

`color-mix()` in a brand token resolves to the property's **initial** value where unsupported —
not to a previous declaration — so a header painted with a brand gradient and white text renders
white on nothing. It looked perfect in Chrome and was invisible on a stock Android WebView, with
every element present in the DOM. Q-Mgr uses `color-mix` in `KioskMode.razor` *with a plain
`rgba()` fallback declaration immediately before it*, which is the correct pattern and was written
for exactly this reason — kiosk hardware running old browsers. **Every `color-mix` on a surface the
app will host needs that fallback**, and it should be checked rather than assumed.

---

## 8. Performance

The app itself is already measured and tuned: 12.5 MB, arm64 only, AOT deliberately **off**
(16.1 MB with profiled AOT), r8 linking, packaging exclusions — and `docs/package-size.md` says
every number was measured by unzipping the artefact rather than read off a build log. Nothing to
add there. What is new is Q-Mgr-specific.

- **Blazor Server over mobile data is the biggest risk, and it is not the app's fault.** Every
  interaction is a round trip over a SignalR circuit, and a dropped circuit shows *"Reconnecting"*.
  This must be measured on a handset on real mobile data before anything is promised. Two things
  already in the codebase matter here: `ConnectionOverlay` covers the page when the health check
  fails and **swallows taps** — and a 429 from `api/v1/health` means *reachable*, which was once
  misread as lost and put a click-swallowing overlay over a working app. §4.2 makes 429s more
  likely on a school's shared address, so those two interact.
- **The first screen after sign-in is the expensive one.** The staff portal computes the whole
  branch's scores for the private rank; the load test measured worst p95 **4.7 s at 200 users** on a
  Debug build, which is why `ComputeBranchCoreAsync` now caches on a fingerprint. On a handset that
  cache is the difference between usable and not, so confirm it is warm for the portal path, and
  remember its one hole: **a raw `ExecuteUpdate` that changes a scored column without stamping
  `UpdatedAt` serves a stale figure.**
- **`@Assets[]` fingerprints at build time**, so a `wwwroot` change needs a rebuild, not a restart.
  This has already produced one wrong measurement in this project; it will produce more when
  someone measures asset weight for the app.
- **Measure, do not eyeball.** `density-check.mjs` does its A/B in one page load precisely because
  two runs of two builds can be faked by a caching difference. Hold mobile measurements to that.

---

## 9. Verification

Three doors already exist in this repository and the app brings a fourth. Nothing here is
verified by a clean build — *"most of this codebase's worst bugs compiled fine."*

1. **The conformance suite is the bar, and it is already written.**
   `python tests/auth_e2e.py --base-url https://qmgr.cashbook.ug --user <u> --password <p>` —
   37 checks over rotation, replay revocation (a replayed token must kill the whole device, not
   just itself), the single-use handoff code, the open-redirect guard, the credential-version kill
   switch, and that the stored value never contains the secret. The ERP passes 37/37. **Q-Mgr is
   not done until it does too.**
2. **`scripts/e2e/mobile-shell-e2e.mjs`, wired into `class-teacher-e2e.sh` in the same commit that
   creates it** — Node, because the risk is concurrency: two simultaneous refreshes from one device
   must rotate once, and a replayed token must revoke. *"A suite nothing calls reports nothing"*,
   and *"a suite that cannot fail reports nothing either"* — set `process.exitCode`.
3. **`scripts/e2e/browser/getapp-page.mjs`** — the login-page link, the download page, the QR, the
   empty state, and that the page does not bounce to `/login` (reproduce by restarting the Web
   process under an open tab, never by fresh navigation).
4. **`node scripts/e2e/route-audit.mjs`** after the routes land.
5. **A real handset, against the dev tenant.** Sign in, let the access token expire, background the
   app, come back, switch workspace, print a ticket to a real thermal printer, take a push. The
   shell's own status line is honest about this and Q-Mgr's must be too: *"Nothing has been
   exercised on a physical device or against a live tenant, and no real thermal printer has been
   tested."*

**Seed what the path needs.** Creating a ticket, a visitor check-in or a welfare record to exercise
a path is a normal setup step, not a blocker — and the pattern that does *not* count is verifying
the paths that happened to have data and listing the rest as "fixed but not exercised live."

---

## 10. Phasing

Each stage is independently useful, and the app degrades rather than breaks, so this can stop at
any line and still be ahead.

| Stage | What lands | Worth on its own |
|---|---|---|
| **A** | Copy, rename, `brands/qmgr.json`, split the icon layers, store text | An app that is Q-Mgr's |
| **B1** | `tenant/info` + login adapter | The app opens and signs in |
| **B2** | `UserDeviceSession`, refresh, logout, device list | **Sessions survive a shift — the one users feel** |
| **B3** | `web-handoff` + `/mobile-session` | No second sign-in |
| **B4** | `app/update`, `app/releases`, `/getapp`, login link | Distribution — and the download links the user asked for |
| **B5** | `branding/product-logo`, attribution flag | The app wears the school's identity |
| **C1** | base64 in the shim; workspace key per host+school; PWA suppression | Printing actually prints; tenants do not cross |
| **C2** | Push: platform FCM, `checkin`, inbox on real notifications | The largest functional gain — **and an iOS submission requirement** |
| **C3** | Kiosk mode, native QR, biometric unlock | The device-level case, and iOS's 4.2 answer |
| **D1** | Windows artefact path in `release-info.ps1` / `build-app-host.ps1` | Windows can actually be published |
| **D2** | Apple Developer Program, macOS build, iOS submission | The third platform, once C2/C3 make 4.2 passable |

**Android and Windows can ship from B4; iOS cannot ship before C2 and C3.** That is not a
ranking of platforms — it is that nobody reviews an APK and Apple reviews everything, so
submitting iOS before push and printing exist is submitting the thing guideline 4.2 rejects
(§6.2).

**B4 is where the user's explicit ask lands** — *"the download links should appear on login, same
way it is working on erp"* — and it depends on B1 only for the workspace address it prints. It
could be pulled forward if the links matter more than the app does yet.

---

## 11. Decisions — settled 2026-09-22

| # | Question | Answer |
|---|---|---|
| 1 | Android `applicationId` | **`ug.qmgr`** |
| 2 | Play listing or out of store | **Out of store for now** |
| 3 | Is the app's build pipeline at risk | **No — it works and has shipped.** I asserted this without checking; see §0 |
| 4 | What a bare workspace code expands against | **`cashbook.ug`**, with full white-label domains alongside (`dashboard.maryhillug.net`) |
| 5 | Q-Mgr artwork | **It exists** — vectors in `Q-Mgr.Web/wwwroot`; SACC parent art at `D:\SACC_SOFTWARE\ART WORK`. §A3 |
| 6 | Platforms | **All three — iOS, Android, Windows** |
| 7 | Workspace identity | **Per host AND school** |

### What those answers surfaced, and what still needs a call

Three consequences that follow from the decisions rather than from the original plan. None is a
blocker; each is a choice somebody has to make once.

1. **A Q-Mgr subdomain needs provisioning, and the ERP owns the wildcard** (§4.1). There is no
   wildcard DNS, and `*.cashbook.ug` is the ERP's server block, so an unprovisioned
   `maryhill.cashbook.ug` lands on the **ERP** rather than erroring. Each Q-Mgr subdomain needs a
   DNS record and an exact nginx block — which is cheap, because the wildcard certificate already
   covers it, so it is the activator's fast path with nothing to issue. **The open question is who
   keeps the one list of subdomains now that two products share the zone.**
2. **iOS cannot be out of store** (§6.1). The App Store, TestFlight and Apple Business Manager
   Custom Apps all go through Apple review, so guideline 4.2 is a gate rather than a deferred risk,
   and push and printing have to exist before submission. **The call is sequencing** — the plan puts
   iOS after C2/C3 for that reason. An Apple Developer Program account and a macOS build machine
   are prerequisites with their own lead time.
3. **Windows and iOS have no artefact path in the publish tooling** (§6.1). `release-info.ps1`
   reads an APK with `aapt`; `build-app-host.ps1` stages `.apk` and `.tar.gz`. With all three
   platforms in scope that is real work — stage D1 — and it must keep the existing rule: read the
   version out of the artefact, never type it.

**And one thing to back up as its own task:** out of store there is no Play App Signing, so the
Q-Mgr keystore is the only key that can ever sign an update for an installed device. Losing it
means every device must uninstall and reinstall, which erases what is on them.

---

## Sources

Platform and standards claims above are from:

- [Meet Google Play's target API level requirement](https://developer.android.com/google/play/requirements/target-sdk)
- [Android developer verification — timeline and consoles](https://developer.android.com/developer-verification)
- [Use of the REQUEST_INSTALL_PACKAGES permission](https://support.google.com/googleplay/android-developer/answer/12085295)
- [Google Play Data safety section](https://support.google.com/googleplay/android-developer/answer/10787469)
- [Google Play Families Policies](https://support.google.com/googleplay/android-developer/answer/9893335)
- [Preparing .NET MAUI apps for Google Play's 16 KB page size requirement](https://devblogs.microsoft.com/dotnet/maui-google-play-16-kb-page-size-support/)
- [RFC 9700 — Best Current Practice for OAuth 2.0 Security](https://www.rfc-editor.org/info/rfc9700/)
- [OWASP MASVS](https://mas.owasp.org/MASVS/) and [MASVS-STORAGE](https://mas.owasp.org/MASVS/05-MASVS-STORAGE/)
- [Learn about Custom Apps in Apple Business Manager](https://support.apple.com/guide/apple-business-manager/axm58ba3112a/web)
- Apple App Store Review Guideline 4.2 (minimum functionality) — as summarised by
  [MobiLoud's review of webview-wrapper rejections](https://www.mobiloud.com/blog/app-store-review-guidelines-webview-wrapper/)
