# Secure document sharing — implementation plan

**Status:** planned, nothing built. **Written:** 2026-09-14. **Revision 2** (library-scoped).
**Designed artifact:** https://claude.ai/code/artifact/4ea724da-8aed-4762-9c96-6b4c5b3de2f8

Requested as: share an uploaded document securely with people who may have no login, track views and
downloads, set rules (read-only vs downloadable), set a password, schedule when the link expires, and
keep an audit log — universal across modules, with the worked example of a school sharing the minutes
of a staff meeting.

**Revision 2 followed a user correction and is the binding version.** Rev 1 shared records in place
via a polymorphic `ResourceType`/`ResourceId` across Welfare, Content, Broadcasts and Docs. The user
rejected that: *"do not mix this feature with existing reports or records… if user wants to share a
generated report, it should be exported to pdf and moved into the sharing repository, whereas the
same document can be flagged to display in the signage, or for restricted share to public."* The
model below is scoped to the Library accordingly, and it is materially smaller as a result.

---

## 1. The model: publishing is the boundary

Nothing is shared in place. A welfare record, a queue report, a student picture — none of them gain a
share button. To share something you **publish a document into the Library**, and the Library is the
only surface sharing knows about.

    Records & reports  ──▶  Publish to Library  ──▶  Document Library  ──┬─▶  Signage (playlist)
    (live, permissioned)     (permission +           (stable document,   │
                              audit row)              own lifecycle)     └─▶  Restricted share link

Two distribution flags, independent. The same document can be on a foyer screen *and* behind a
passcoded link. What it cannot do is leak from one into the other, because **the serving mode follows
the flag, not the file**.

**This deletes the hardest problem in rev 1.** Sharing records in place meant every share had to
re-derive the source record's permission set and row scope — including whether a class teacher could
share a document about a child outside their class, and how `WelfareVisibility`'s three rungs mapped
onto link audiences. All of that is gone. **A safeguarding record cannot be link-shared at all**,
because sharing only knows about the Library, and getting into the Library is an explicit act by
someone holding the permission to perform it.

---

## 2. BLOCKING — uploads are served ahead of any authorisation check

Found while scoping this, and confirmed on 2026-09-14.

Uploads live under the API's own `wwwroot`, so the static-file middleware serves them before
authentication runs; there is no per-file authorisation on that path.

> **Detail is held locally, on purpose.** This repository is public, so the reproduction, the
> affected middleware lines and the list of exposed data are in `SECURITY-UPLOADS.local.md` in the
> repository root — untracked, covered by `*.local.md` in `.gitignore`. Read it before starting
> Phase 1. Fold it into `docs/TASK_TRACKER.md` and delete it once the fix ships.

Two consequences:

1. A permission check on the record that owns a file is **meaningless** while the bytes are also
   reachable directly, and an upload URL, once it leaks, is permanent, unrevocable and leaves no
   audit trail.
2. Specific to this feature: a Library document marked *restricted share* would sit in exactly the
   same folder as one marked *signage*, so the restriction would be decorative.

OWASP's rule is that uploaded files should never be directly reachable, and that public access
belongs behind a handler mapping an id to a file. **The distribution flag can only decide how the
bytes are served once serving goes through code**, which is why this is Phase 1 and not a
nice-to-have ordering preference.

---

## 3. Prior art

| Control | DocSend | SharePoint / OneDrive | Papermark | Q-Mgr plan |
|---|---|---|---|---|
| Passcode on link | Yes | Yes, per link | Yes | Phase 4, hashed |
| Expiry date | Yes | Yes, org-enforceable max | Yes | Phase 4, + start date |
| Instant revoke | Yes | Yes | Yes | Phase 3, DB flag |
| Disable download | Yes | Yes (view-only) | Yes | Phase 5, server-side |
| Email capture / verify | Yes — the attribution mechanism | No; anonymous links log IP only | Yes | Phase 4, OTP |
| Per-page dwell analytics | Yes — its differentiator | No; open/access events only | Yes | Phase 6 |
| Dynamic watermark | Downloads only | Not native | Downloads | Phase 5, on-screen + print |
| Audit retention | Product analytics, not an audit trail | 180 days default, 1 yr on E5 | Self-hosted | Phase 6, explicit matrix |
| Same doc to a public display | Not a use case | Not a use case | Not a use case | Playlist membership — exists today |

All four reference systems share this plan's separation: they distribute documents from a repository,
not rows from a business system.

**Net:** adopt DocSend's control set and Papermark's per-page analytics, with SharePoint's discipline
about the log being an *audit* record with a stated retention period rather than a growth dashboard.

### Say plainly what "view-only" can and cannot do

Anything rendered on a screen can be photographed, screenshotted, or pulled from the network tab. The
DRM vendors sell against exactly this gap and their own answer is not prevention but **deterrence
through attribution** — a watermark carrying the viewer's identity so a leaked copy names its source.

So promise two things Q-Mgr can keep: **the file bytes are never sent to a view-only viewer**, and
**every view is attributed and logged**. Do not claim to prevent copying. Writing "downloads
disabled" beside a control that only hides a button would be a UI that lies, which this codebase has
a documented history of hunting down (see `CounterTerminal`'s Complete/No-Show, Phase 70).

---

## 4. Getting a report in — PDF export without breaking the no-dependency rule

Two standing constraints collide here: printing is the browser's job with no PDF rendering library on
the server or in the page, and no third-party server dependencies at all. A server-side PDF renderer
is out.

The rule has an explicit carve-out that resolves it: *pure client-side libraries loaded via CDN ship
to the browser, not the server* — which is how PDF.js, page-flip and SheetJS are already here.

- **Preferred:** a pinned client-side PDF library from cdnjs renders the existing print-styled report
  route and posts the bytes to the Library. The A4 welfare report already has its own route on
  `MinimalLayout` and its own `@media print` rules to build on.
- **Fallback:** the browser's own Save as PDF, then upload — works today with no code at all. Keep it
  documented for anything the generator renders badly.
- **Not proposed:** server-side rendering via a headless browser or converter. Same category as the
  LibreOffice option declined for PPTX, declined for the same reason.

### A published document is a snapshot, and the UI must say so

Once exported the document stops tracking the record it came from. A welfare summary published in
March and shared in September shows March's figures. The Library entry therefore carries **what it
was generated from and when**, shown on the share page itself — the same reasoning behind
`WelfareSummaryDto.ScopedToClasses`, where a correct figure read as something it wasn't was the
actual bug.

---

## 5. Architecture — the link is not a signed token

This codebase already has a time-limited signed-token primitive: `VisitorBadgeTokenService` uses
`ITimeLimitedDataProtector` for badge QR codes. Reusing it directly for share links would be wrong
for one decisive reason — **a self-contained signed token cannot be revoked.** Revocation is a hard
requirement, and a stateless token can only be withdrawn via a blocklist, which is a database lookup,
which is the thing the stateless design was avoiding. A six-month link must also survive a key-ring
rotation; a badge valid for hours need not.

| Piece | Decision |
|---|---|
| Share slug | 160 bits of `RandomNumberGenerator` entropy, base64url. The DB stores only a SHA-256 hash — API-key discipline, so a database leak does not hand over working links. Lookup by hash. |
| Public route | `/s/{slug}` on Q-Mgr.Web, `[AllowAnonymous]` on `MinimalLayout` — the pattern `Docs.razor` established. Short, because these get typed off a printed page. |
| Serving mode | Decided by the distribution flag, not the folder. **Signage** documents stay publicly fetchable — you chose to put them on a wall. **Share-only** documents stream solely through the gate, and their raw path returns 401. |
| Content token | Here `ITimeLimitedDataProtector` *is* right: once a viewer clears the gates the page gets a 60-second token to stream bytes. Short-lived by nature, so key rotation is a non-issue and the durable path is never exposed. |
| Link origin | Built from the public origin, never `ApiBaseUrl`. That is the loopback bug class this project has been bitten by twice — signage PDFs and welfare evidence uploads both shipped links pointing at `127.0.0.1`. |

---

## 6. Data model — one table enhanced, two added

Scoping sharing to the Library collapses most of rev 1's data model. **There is no polymorphic
`ResourceType` any more** — one foreign key, to one table.

And the Library already exists. `MediaContent` is organization-scoped, already carries
`ContentType.Pdf`, name, description, MIME type, size, thumbnail, tags and storage fields, and its
`PlaylistItems` navigation **is** the signage path. Signage distribution therefore needs no new
mechanism at all: adding a document to a branch playlist is how it reaches a wall today, and the
flip-book viewer rebuilt in Phase 83 already plays PDFs there.

- **`MediaContent` — enhanced, not replaced.** A few nullable columns: `IsShareable`,
  `PublishedFrom` / `PublishedAt` (snapshot provenance), and an optional public `Summary` for the
  share page. Widening an existing table is this project's stated first choice and this is the clean
  case for it.
- **`DocumentShare` — new.** One document needs many concurrent links with different rules and its
  own lifecycle: issued, gated, revoked, expired. Holds `MediaContentId`, `SlugHash`, `PasscodeHash`,
  `NotBefore`, `ExpiresAt`, `MaxViews`, `AllowDownload`, `RequireEmail`, `WatermarkMode`,
  `RevokedAt`, `RevokedBy`.
- **`DocumentShareEvent` — new.** Append-only, one row per real attempt: issued, opened, passcode
  failed, email verified, page viewed, downloaded, denied, expired, revoked. `NotificationLog` is the
  precedent, including its hard-won rule — a *skipped* outcome writes no row, because logging
  non-events as failures is how a log stops being read.
- **Recipients — no table.** An optional JSON allow-list of addresses on the share, with the verified
  address recorded on each event, attributes views without a join table. Per-person links (DocSend's
  model) become worth a table only if per-person revocation is later wanted.

**Three catalogues, as always.** New codes — `library.publish`, `documents.share.create`,
`documents.share.manage`, `documents.share.audit` — go into `RbacSeeder.AllPermissions`,
`Domain/Constants/Permissions.cs` **and** `Web/Services/IPermissionService.cs`.

---

## 7. Access rules — the permission that matters is the one to publish

Because a link can only ever point at a Library document, the sensitive decision moves to the moment
of publishing. That is where the record's own rules still apply, and the only place they need to.

- **You can only export what you can already read.** Publishing a report runs the same permission and
  row-scope checks as viewing it — so a class teacher exports their own classes' figures and the
  published PDF states the scope on its face. **This is where `IStudentScopeService` belongs; nowhere
  in the sharing code.**
- **Publishing is its own permission** (`library.publish`), separate from reading the source and from
  creating a link. Being able to see a report is not the same as being able to turn it into a
  document that leaves the building.
- **Every publish writes an audit row** naming the source, the person and the moment. If a document
  later turns up where it should not, that row is how it got out.
- **Fail closed.** A share whose document is missing, deactivated, or no longer flagged shareable
  refuses — it does not fall back to serving the file.
- **Revocation beats everything**, checked per request rather than cached, so "revoke" means now.
  `PermissionAuthorizationHandler` caches for five minutes; share state must not join it.

---

## 8. The audit trail

An audit log containing IP addresses is personal data — the CJEU settled that in *Breyer*, and GDPR
Article 5's storage-limitation principle applies to it like anything else. A log kept forever "just
in case" is a liability, not diligence.

| Tier | Contents | Retention |
|---|---|---|
| Always captured | Share id, event type, UTC timestamp, document id, outcome, plus the publish event itself. No personal data beyond the internal actor id. | Indefinite |
| Captured when present | Verified email, truncated IP, coarse user agent, per-page dwell. | **6 months** proposed, matching CNIL guidance for active logs holding personal data |
| Purge | Hangfire recurring job blanks the attribution columns past the window, keeping the event row. Hangfire is already on PostgreSQL storage, so it survives a restart and adds no dependency. | — |
| Readable by | `documents.share.audit` — deliberately narrower than the permission to create a link, because an access log is a record of people's behaviour. | — |

Per-page dwell comes almost free: the PDF viewer rebuilt in Phase 83 already reports every page
change to the server through `OnPageFlipped`.

---

## 9. Worked example — minutes to staff who have no login

1. Someone with `library.publish` uploads the minutes PDF to the **Document Library** — or, if the
   minutes were generated in Q-Mgr, exports them there in one click.
2. They tick **Shareable**. They could also tick signage and add it to the staffroom playlist; here
   they do not.
3. **Create link**: expires 31 Dec 2026, passcode on, download off, email required. Dates render
   through `QDateFormat` — day-first, so `03/09/2026` can never be read as 9 March.
4. Q-Mgr issues `https://qmgr.cashbook.ug/s/{slug}` and emails it through the platform mailbox every
   tenant already falls back to. The passcode goes by a separate channel and the UI says so —
   emailing both defeats the point.
5. A staff member opens it, enters the passcode, verifies their address with a one-time code, and
   reads the minutes in the flip-book viewer with their address watermarked across each page. The PDF
   bytes never reach their browser.
6. The office sees who opened it, when, and which pages they read.
7. On 31 Dec the link stops working by itself. If a laptop is lost in November, one click revokes it
   immediately — and the log still shows everything that happened before then.

---

## 10. Phases

**Phase 1 — Close the open store.** BLOCKING, breaking change.
- Move uploads out of `wwwroot` to `/var/lib/qmgr/uploads`; serve through a controller that decides
  by distribution flag — signage public, everything else gated.
- Backfill stored URLs onto the new route. `Infrastructure/Data/UploadLinkRepair` is the working
  precedent, including rewriting links embedded in `doc_articles.BodyHtml`.
- Ship the deploy half in the same change: `ReadWritePaths` in the unit, the directory created and
  `chown`ed by `install.sh`, the nginx `/uploads/` block reworked. The Web key-ring fix is the
  cautionary tale — its one-line version would have made production worse.
- Outside `$InstallRoot` deliberately, same reasoning as the Data Protection key ring: a store inside
  the install directory is replaced on every deploy.
- Verify by fetching a welfare attachment unauthenticated and getting 401 where today it returns 200.

**Phase 2 — The Library as a document surface.**
- Nullable columns on `MediaContent` for shareable, provenance and public summary; a Documents view
  in the Media Library UI filtered to `ContentType.Pdf`.
- `library.publish` in all three catalogues; a publish audit row.
- Signage needs nothing new — the playlist path already carries PDFs.

**Phase 3 — The link.** 2 tables + 1 migration.
- `DocumentShare`, `DocumentShareEvent`, and `IDocumentShareService` as the single home for issue /
  resolve / revoke.
- Public `/s/{slug}` route; create and revoke from the API.
- Expiry and revocation only at this stage — worth shipping alone, because an expiring revocable link
  already beats emailing an attachment.

**Phase 4 — Gates.**
- Passcode hashed with ASP.NET Core's `PasswordHasher`, never stored or logged in clear, and never
  emailed alongside the link.
- Email verification by one-time code through the platform SMTP account, reusing
  `ISmtpProfileResolver`.
- Availability window (`NotBefore` as well as `ExpiresAt`), max views, one-time links.
- Per-share attempt lockout on top of the `UseIpRateLimiting` already in the pipeline. A four-digit
  passcode with unlimited attempts is not a passcode.

**Phase 5 — Viewer rules and watermarking.**
- View-only renders pages to images in the flip-book viewer; the PDF itself is never served. The
  "open original" link and the download control are withheld **server-side**, not hidden in markup.
- Watermark drawn onto each rendered page with the viewer's verified identity and timestamp —
  client-side canvas, no server rendering dependency.
- Download, when allowed, streams through the controller so it is logged and counted.

**Phase 6 — Publish-to-Library from reports, and audit.**
- A "Publish to Library" action on report routes: client-side PDF generation from the existing
  print-styled markup, uploaded straight into the Library with provenance recorded.
- Activity view per document and per share — opens, unique viewers, per-page dwell, denials — plus
  CSV export behind `documents.share.audit`.
- Retention purge on Hangfire; a bell and optional email when a share is first opened, through the
  existing dispatch path.

---

## 11. Decisions needed before building

1. **Does the Library stay one thing, or split?** Proposed: enhance `MediaContent` so one document
   can be both signage and a share, which is what was described. The alternative is a separate
   documents table — a migration and a second concept, but it keeps marketing media apart from
   official documents.
2. **Retention for the attribution tier.** 6 months proposed. A school may be obliged to keep some
   records considerably longer.
3. **Maximum link lifetime.** SharePoint lets an administrator cap it org-wide. Worth having, and the
   cap belongs to the tenant, not the person creating the link.
4. **Phase 1 is a breaking change to stored URLs.** It needs a deploy window and the backfill must run
   before anything re-reads those links. Confirm the sequencing rather than a feature flag.

---

## 12. Verification

There is no test project and that is the decision; a clean build is not verification. Extend
`scripts/e2e/class-teacher-e2e.sh` rather than starting a second script. The assertions that matter
are the negative ones:

- an expired link refused;
- a revoked link refused, immediately;
- a passcode brute-forced and locked out;
- a share-only document's raw path returning 401 **while a signage document's still serves**;
- a user without `library.publish` refused the export;
- a scoped caller's published report carrying its scope on its face.

---

## Sources

- [OWASP — File Upload Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html) — store outside the webroot, serve via an id-to-file handler, random filenames
- [OWASP — Secure Cloud Architecture Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Secure_Cloud_Architecture_Cheat_Sheet.html) — signed URLs and their expiry trade-offs
- [DocSend — sharing controls](https://www.docsend.com/features/sharing/) and [how it works](https://www.docsend.com/how-it-works/)
- [Microsoft Learn — sharing auditing in the audit log](https://learn.microsoft.com/en-us/purview/audit-log-sharing) — `AnonymousLinkUsed`, and that anonymous access logs IP rather than identity
- [Petri — tracking anonymous access to SharePoint and OneDrive documents](https://petri.com/tracking-anonymous-access-sharepoint-onedrive-documents/)
- [Papermark](https://github.com/mfts/papermark) — open-source DocSend alternative; link gating plus per-page dwell analytics
- [Locklizard](https://www.locklizard.com/sharing-documents-securely/) and [Peony — dynamic watermarking](https://www.peony.ink/blog/dynamic-watermarking-guide) — attribution as the realistic control, not prevention
- [GDPR, IP logging and retention](https://optimizesmart.com/blog/gdpr-ip-address-logging-retention-and-monitoring/) — *Breyer*, dynamic IPs as personal data
- [GDPR log retention and disposal](https://logcentral.io/en/blog/gdpr-compliance-log-data-retention-disposal) — retention matrices, CNIL's 6-month guidance
