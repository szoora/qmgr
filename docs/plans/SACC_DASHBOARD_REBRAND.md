# SACC Dashboard — the rebrand from Q-Mgr

Artifact (the designed version, with the logo concepts drawn): https://claude.ai/artifact/F21ZYLMNNsGtTRt1KxViqN
Revision 4, 24 Sep 2026. **All ten decisions (B1–B10) accepted as recommended, 24 Sep 2026** ("take the recommendations").

## 1. The names

- **Our product is "SACC Dashboard"**, spaced (B1). The joined form is for domains and handles only.
- **A school's app carries its own name, exactly as typed.** `Organization.BrandName` IS the app's name (its hint
  always said "overrides Q-Mgr wherever the product names itself"). Nothing is forced into it — not "Dashboard",
  not SACC. We SUGGEST "WORD Dashboard" at registration and on Appearance; the school may accept, change or clear it.
- **Shown only while white-labelling is entitled AND switched on** (B2). Otherwise "SACC Dashboard".
- **The only credit is "© 2026 SACC".** Never "Powered by". With attribution removal the line credits the school.
- **`Organization.Name` is the organisation** (Settings → General): printed headers, "the school" in emails, the
  mobile app's company line. The readers that used `BrandName ?? Name` for that move to `Name`.

| Situation | App name | Footer |
|---|---|---|
| Platform host / not white-labelled | SACC Dashboard | © 2026 SACC |
| White-label on + entitled, Brand Name set | as typed | © 2026 SACC |
| … plus attribution removal | as typed | © 2026 {Organization} |
| White-label on, no Brand Name | SACC Dashboard | © 2026 SACC |

## 2. The rule for a Brand Name (B3)

`ProductBrand.ValidateBrandName`, run by the API, the registration form and the Appearance page:
2–40 characters, one line, stored and shown as typed; refused if it contains "SACC" (it would read as ours).
Not unique across tenants. A stored value that fails the rule is ignored when resolving (falls back to
SACC Dashboard), never rewritten. `ProductBrand.ShortNameFor` gives ≤12 characters for a home-screen label:
the whole name when it fits, else its first word.

## 3. Single source of truth

`Q-Mgr.Shared/Application/Branding/ProductBrand.cs` holds the company, descriptor, legal entity, tagline, website,
support address, asset paths, `NameFor`, `ShortNameFor`, `ValidateBrandName`, `SuggestFor`.
`scripts/e2e/brand-literal-check.mjs` (in `guards.sh`) fails on "Q-Mgr", "SACC Dashboard", "SACC Software" or
"Powered by" typed anywhere else in `src/` outside comments, migrations and a named allow-list.

**Never renamed (wire formats / identities):** `SetApplicationName("QMgr")`, JWT issuer `qmgr-api` / audience
`qmgr-clients`, Android `applicationId ug.qmgr`, DB schema `qmgr`, namespaces and projects, `qmgr-*` localStorage
keys, service names and install paths.

## 4. Decisions (all accepted)

| | Decision |
|---|---|
| B1 | "SACC Dashboard", spaced |
| B2 | A school's name shows only with white-label entitled + on; "© 2026 SACC" stays unless attribution removal |
| B3 | Free text 2–40, one line, as typed; refuse "SACC"; no uniqueness |
| B4 | Registration asks, optional, with a suggested "WORD Dashboard" |
| B5 | Tenant admins (`settings.edit`) and the platform may change it |
| B6 | Logo concept A — an S made of dashboard tiles, wine, flat |
| B7 | Tab title suffix is the app name |
| B8 | The home page is labelled "Home" |
| B9 | Mobile app: rename display name and icons; keep `ug.qmgr` |
| B10 | "Front Office" retired as a suffix; tagline "Staff, welfare and front office" |

## 5. Phases

1. `ProductBrand` + the guard + `wwwroot/brand/` assets.
2. Sweep every literal onto `ProductBrand`; platform manifest served by `/app-manifest.json`; seeded names migrated.
3. Brand Name: readers of the organisation's name move to `Name`; validation; `ProductName`/`ProductShortName` on the
   branding DTOs; registration, Appearance and Platform → Tenants fields.
4. `QProductMark` and the surfaces: tab title, manifest, emails, print footers, sidebar, loading screen.
5. "Dashboard" → "Home" (label only; route `/` unchanged).
6. Mobile app display name and icons; CLAUDE.md.

Verification: the guard; API e2e section; browser suite `product-name.mjs`.
