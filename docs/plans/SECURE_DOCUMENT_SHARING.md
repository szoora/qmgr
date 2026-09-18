# Secure document sharing — implementation plan

**Status:** BUILT 2026-09-15 (Phase 84 in `docs/TASK_TRACKER.md`) — all six phases, verified by
section 12 of `scripts/e2e/class-teacher-e2e.sh`. The four §11 decisions were taken as the plan
proposed: the Library stays one table (`MediaContent` enhanced); 6 months' attribution retention,
tenant-editable; a tenant-wide link-lifetime cap (365 days by default, 0 = no cap); and Phase 1
turned out not to be a breaking change at all — the URL path was kept and only the disk location
moved, so no backfill and no deploy window were needed. **Written:** 2026-09-14. **Revision 2**
(library-scoped). Sections below are the design as built; where the build departed, CLAUDE.md's
"Secure document sharing" section is the binding text.

**Revision 4 — 2026-09-18, the audit's recommendations BUILT.** Every finding in §14 and every
duplication in §13 that could be fixed in code has been, plus D5, D6 and D7. What landed, and the
decisions taken:

- **D5 — gate it *and* refuse the combination.** `MediaServing` is the one home for the serving rule
  and reads `IsShareable` alone. Refusing at both write paths is not belt-and-braces: gating without
  the refusal would make a shared document silently stop rendering on a wall, because a public
  display fetches anonymously.
- **D6 — retire, do not delete.** A document with share history is deactivated and un-shared; its
  bytes go, its links die, its audit rows stay. A document nobody ever shared is still deleted outright.
- **D7 — three classifications, most restrictive wins, locked against downgrade.**
  `General / Internal / Confidential` on the document, narrowing the tenant policy and never widening
  it. Raising needs `library.publish`; **lowering needs `documents.share.manage` and a reason of ten
  characters, kept on the row**. Defaults: Internal caps links at 90 days and forces email
  verification; Confidential caps at 30 days, forces verification, withholds download, and keeps
  attribution for 730 days — the differentiated retention of §14 finding 5.
- **A bug the new e2e caught in the new code:** the first cut folded the tenant's `AllowDownloadByDefault`
  and `RequireEmailByDefault` into the effective rule, so a *General* document asking for
  `allowDownload: true` came back with download withheld. Those two are DEFAULTS for a new link, not
  constraints; only the classification rule constrains. `EffectiveFor` says so now.
- **Device-binding (§14 finding 6) was NOT implemented, deliberately.** DocSend binds its verification
  *link* to a device because the link itself grants access. Here the emailed six-digit code is useless
  without the challenge token, which is returned only to the browser that asked and posted back with
  the code. Forwarding the code already achieves nothing, so a device heuristic would add false
  negatives on a network switch and no security.
- **Verified:** sections 0–14 **494 / 0**, section 15 **263 / 0**. Six new assertions cover the
  combination §12 never tested, the classification rules, and AU-9 (the audit trail surviving a delete).

**Revision 3 — 2026-09-18, post-build audit.** Asked to confirm single-source-of-truth and no
duplication, and to re-check the design against credible sources. Three things changed:
§1 carries a **correction** — this plan contradicted itself about a document that is both signage and
shareable, and the build followed the unsafe half; **§13** is the SSoT and duplication audit (the
architecture holds, three implementation copies have drifted); **§14** is what the audit found,
ranked. §3, §8 and §12 were re-grounded on primary sources — including Uganda's Data Protection and
Privacy Act 2019, which governs these tenants and which rev 2 did not cite. Nothing in §§1–12 was
deleted; corrections are marked in place.
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
passcoded link.

> **CORRECTION, Revision 3 (2026-09-18) — this is the one place this plan contradicted itself, and
> the build followed the wrong half.** Rev 2 said here that a document which is both "cannot leak
> from one into the other", while §5 below defined serving mode as two *exclusive* categories
> (signage → public, share-only → gated) and gave no rule for both at once. §12's verification list
> encoded the same binary, so the e2e asserts "a shareable document **on no playlist** is gated" and
> "a plain upload is public" — and never the combination. The implementation resolved the ambiguity
> as `IsPublic: !IsShareable || OnSignage` (`UploadAuthorizer.cs:195`): **signage wins.** A document
> that is both is served anonymously at its raw `/uploads/media/{file}` path, with a day of cache.
> The passcode still guards `/s/{slug}`; the bytes are not guarded at all.
>
> **The invariant is stated once, here, and nowhere else.** A document's serving mode is decided by
> `IsShareable` alone: **if it is shareable, its raw path is gated, whatever else is true of it.**
> Playlist membership makes a document *playable on signage*; it does not make it public.
>
> **Decision needed (D5 in §11).** The honest alternative is to refuse the combination outright — a
> document on a wall in a public foyer is readable by anyone standing there, so "passcode-protected
> **and** on the wall" is a contradiction the product should reject at the point somebody asks for
> it, rather than silently picking a winner. Either resolution is defensible; the current one was
> never chosen.

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

### Revision 3 — the comparison re-done against primary vendor documentation (2026-09-18)

Rev 2's table was built from feature pages. Re-checked against the vendors' own docs, three things
changed, and two of them are in this product's favour.

- **Store-once-and-reference is universal.** SharePoint, Drive, Box, Dropbox and DocSend all point a
  link at one stored item. The only copy-per-recipient behaviour found anywhere was Google
  Classroom's "Make a copy of the file for each student" — an *authoring* feature, not distribution.
  MoReq2010 gives the reason in records-management terms: it "makes no provision for the copying of
  records", because "When a copy is made of a record, it loses part of its event history and does not
  have parity with the original."
- **Attribution: this product beats the two largest platforms, and they say so themselves.**
  Microsoft, on its own anonymous links: *"People using an Anyone link don't have to authenticate,
  and their access can't be audited."* Google: external actions "are shown as **anonymous** except
  when the item is explicitly shared with them." Email verification before view is exactly what turns
  an anonymous capability into an attributable one — the same move DocSend makes, on its Advanced
  tier.
- **Expiry: ahead of both defaults.** Google's default sharing is indefinite and its access-expiry
  feature is limited to eligible work/school accounts; SharePoint's expiry policies for internal link
  types are a 2024–2026 addition. A link that expires by default is not the norm it looks like.
- **"View-only is attribution, not prevention" is the vendor-documented norm**, not a weakness in
  this product's honesty. Dropbox: disabling downloads "doesn't prevent people from saving the
  content using other methods." Box's watermarking page and Microsoft's block-download page make no
  capture-prevention claim either. This plan's wording is more explicit than any of them.
- **Where the comparators genuinely go further:** a **sensitivity label on the document that
  constrains the share**. Microsoft Purview sets `DefaultSharingScope` and `BlockDownloadPolicy`
  *per label*, and where a site default and a document label disagree, "the more restrictive scope
  settings will be applied". Google applies labels by DLP rule and **locks them against user
  downgrade**. This product's policy is one level coarser — a tenant cap and tenant defaults, with
  nothing that can say *this particular document is safeguarding material and may never be shared
  with download allowed*. See §14, finding 4.
- **The school-MIS comparison is the most directly relevant, and the bar is low.** Arbor states in
  its own help centre that publishing to the parent portal "does not send a push notification, email
  or SMS — if you need parents to act promptly, send a separate message"; Bromcom's push notification
  is an opt-in tickbox per publish; neither documents a view audit. Notifying on share, and recording
  who opened it, puts this product ahead of both.

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
| Serving mode | Decided by the distribution flag, not the folder. **Signage** documents stay publicly fetchable — you chose to put them on a wall. **Share-only** documents stream solely through the gate, and their raw path returns 401. **Rev 3:** these two were written as if they were exhaustive. They are not — a document can be both, and §1's corrected invariant is what governs that case. Do not restate the rule here. |
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

### Revision 3 — the statute that actually governs this, and the control that licenses it

Rev 2 reasoned from *Breyer* and CNIL. Both still apply, but the binding law for a Ugandan tenant is
Uganda's own, and it is more explicit than GDPR about what must happen at expiry.

- **Uganda, Data Protection and Privacy Act 2019 (Act 9 of 2019, now Cap. 97), s.18(1)** — personal
  data shall not be retained "for a period longer than is necessary to achieve the purpose for which
  the data is collected and processed", subject to the lawful-purpose and legal-requirement
  exceptions in (a)–(d). **s.18(4):** "A data controller shall destroy or delete a record of personal
  data or de-identify the record at the expiry of the retention period." **s.18(5):** that destruction
  "shall be done in a manner that prevents its reconstruction in an intelligible form."
  **Blanking the attribution columns while keeping the event row is de-identification under s.18(4)
  — the option the statute explicitly allows — not a half-measure.**
- **s.3(1)(c) minimality** ("adequate, relevant and not excessive") is near-identical in wording to
  GDPR Art. 5(1)(c), so the rev 2 reasoning carries over rather than being replaced.
- **s.24(1)(c)** gives a data subject the right to be told "the identity of a third party or a
  category of a third party who has or has had access to information", and **reg. 39(7)** of the 2021
  Regulations requires a controller ordered to erase data to notify "all third parties to whom such
  personal data had been previously disclosed". **A share-and-event log is how those two obligations
  are discharged at all** — which reframes the audit trail from a nice-to-have into the mechanism for
  a statutory right. Neither Arbor's nor Bromcom's documented portal flow appears to record who
  viewed a document.
- **NIST SP 800-53 Rev 5 supplies the control language for all three design choices:**
  - **AU-3** — an audit record must establish what, when, where, source, outcome, and the identity of
    those involved. A share event with type, timestamp, slug, truncated address, coarse agent and
    outcome covers all six.
  - **AU-3(3) Limit Personally Identifiable Information Elements** — "Limit personally identifiable
    information contained in audit records to the following elements identified in the privacy risk
    assessment". **Truncating the IP and coarsening the user agent is therefore an enhancement of the
    standard, not a compromise of the log.** Cite this in favour of the truncation, not in apology.
  - **AU-11** — "Retain audit records for [organization-defined time period]". A tenant-configurable
    window is the standard's own model. For scale: Microsoft Purview's default is 180 days and Google
    Drive's log retention is a flat 6 months, so this product's configurable window is more capable
    than either default.
  - **AU-9 Protection of Audit Information** — protect audit records "from unauthorized access,
    modification, and deletion". **This is the control the build currently fails; see §14, finding 1.**

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

### Decisions added by Revision 3 (2026-09-18)

5. **A document that is both shareable and on a playlist — gate it, or refuse the combination?**
   The §1 correction states the invariant as *gate it*. The alternative is to refuse: a document on a
   wall is readable by anyone in the room, so pairing it with a passcode is a contradiction worth
   rejecting at the point of asking. Whichever is chosen, it is one rule in one place, and §12 gains
   the assertion that was never written.
6. **What happens to a share audit trail when its document is deleted?** Today the cascade erases it,
   on `content.delete`, which Manager holds. Options: restrict the delete to a holder of
   `documents.share.manage`; soft-delete the document and keep the shares; or re-parent the events so
   they survive. NIST AU-9 says the log must be protected from deletion; the controller already says
   "never deletes". Something has to give.
7. **Should a document carry a classification that constrains its sharing?** This is the single
   biggest architectural gap against the comparators (§14, finding 4). It is also the prerequisite for
   differentiated retention (finding 5). If yes, the label must be locked against user downgrade in
   the same change.

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

### Revision 3 — the assertions this list was missing (2026-09-18)

The list above is written as two exclusive cases, which is how the ambiguity in §1 reached the suite.
Section 12 of `class-teacher-e2e.sh` implements it faithfully — `:517` asserts "a shareable document
**on no playlist** is gated" and `:528` "a plain upload is public". The untested combination is the
unsafe one. Add:

- **a shareable document that IS on a playlist — its raw path must still refuse** (today it serves;
  this is the §1 correction, and the assertion should be written to fail until that is fixed);
- **`ActiveShareCount` equals the number of links `EvaluateState` calls Active** — set a document
  un-shareable with live links and assert the count goes to zero (today the card still counts them);
- **deleting a document does not destroy its share history** — whatever §14 finding 1 is resolved to,
  assert it, because the cascade is silent;
- **a second, concurrent write to `Organization.Settings` does not drop the sharing policy** — the
  branch-settings race, which §14 finding 2 says applies here too;
- **the publish control is absent, not merely refused, when the module is not held** (§14 finding 3).

---

## 13. SSoT and duplication audit (Revision 3, 2026-09-18)

Asked directly: *does document management and sharing have a single source of truth, with no
duplication?* **The architecture does. The implementation has accumulated duplication at its edges,
and three copies have already drifted.**

### What is genuinely single-source

- **Every DTO in the feature is defined once, in `Q-Mgr.Shared`, and referenced by both projects** —
  17 types checked (`MediaContentDto`, `DocumentShareDto`, the create/update/revoke requests,
  `DocumentShareIssuedDto`, `DocumentShareEventDto`, `DocumentPageDwellDto`, `DocumentActivityDto`,
  `DocumentSharingPolicyDto`, `UpdateMediaPublishingRequest`, the public gate/open/resume/event
  shapes, and the three enums). **No second definition exists on either side.** This codebase's
  documented recurring bug class — a DTO defined in the API and independently again in the Web — is
  not present in this feature. It is the cleanest part of the audit.
- **One home per decision:** slug generation and hashing; all four token purposes (content, session,
  email challenge, upload access); `EvaluateState`; window validation; policy read/write;
  `TruncateIp` / `CoarseUserAgent` (reused verbatim by `ActivityLogger`, not re-implemented);
  link-origin allow-listing; `UploadAuthorizer.ClassifyAsync`; `UploadLinks.Sign`/`Strip`;
  `UploadFileTypes`; and `ContentController.ToDtosAsync` as the single `MediaContent → DTO` mapper.
- **`FilePath` and `FileUrl` each have exactly one writer**, which is the fix that closed the
  `FileUrl`-matching hole — a client-supplied `FileUrl` is accepted only when it is *not* in our own
  store.
- **The four permission codes are byte-identical across all three catalogues**, and no fourth copy
  exists anywhere in `.razor`, `.js`, `.sh`, `.mjs` or `.sql`.
- **The structural anti-duplication choices from rev 2 all held:** no polymorphic `ResourceType`,
  one foreign key, and "Recipients — no table".

### Already drifted — three

1. **`ActiveShareCount` vs `EvaluateState`.** `ContentController.cs:91-97` computes "active links" in
   SQL, checking revoked / active / expiry / view-limit. `EvaluateState` additionally requires the
   document to be present, active, **shareable** and to have a `FilePath`, and checks `LockedUntil`
   and `NotBefore`. The same page therefore shows two different numbers for one fact — the card badge
   and the activity modal. The worst case: turn sharing off and the card still reads "3 live links"
   on a document whose every link refuses. `ContentDto.cs:28` documents the *SQL* behaviour as if it
   were the rule. The fix is not symmetry — `ActiveShareCount` should be the count of
   `EvaluateState(...) == Active`.
2. **`IMediaStorageService`'s two implementations.** The 2026-09-15 security fix — stored extension
   from the `UploadFileTypes` allow-list, `%PDF-` magic-byte check — landed on the local disk
   implementation only. `S3MediaStorageService` takes the client's extension straight through, and
   writes a `FilePath` shape the classifier can never match, so every S3-stored row would classify as
   an orphan. Dormant behind one config key, which is exactly how this kind of thing ships.
3. **Seven `PublishToLibrary` blocks.** Five write a staff export log row; `StaffAppraisalReport` and
   `WelfareTimelineReport` do not — so a published welfare report appears in the Library and in no
   activity log. Three variants of the success wording. Everything but the element id and the
   provenance string is boilerplate.

### Agreeing today, latent tomorrow

- **The "is this file public" predicate lives in two places** — `UploadAuthorizer.cs:195` and
  `ContentController.cs:110` — as exact De Morgan inverses. They agree in every case today. The
  hazard is asymmetric: only the first is load-bearing for security, and a reviewer editing the
  second has no signal that it is half of a pair. Relatedly, `UploadAuthorizer.Invalidate` promises
  in its own doc comment to be called when playlist membership changes, and no playlist endpoint
  calls it — so a document taken off its last playlist stays public for up to the 30-second cache.
- **`Organization.Settings` is read-modify-written by six code paths on two different concurrency
  protocols**, and the sharing policy is on the unlocked one. `Branch.Settings` already learned this
  lesson — CLAUDE.md records "three writers, every one takes `BranchSettingsLock`" — and
  `StaffOnboardingPolicyService` documents the hazard in a comment the other four writers cannot see.
  Two concurrent saves on different keys silently drop one.
- **Two Web clients for one upload endpoint**, already drifted in capability: one cannot send
  `shareable`, the other hardcodes `application/pdf`.
- **Publishing rules stated twice** — `UploadMediaContent` and `UpdatePublishing` — with two wordings
  of the PDF-only rule and two stamping semantics (`=` vs `??=`).
- **Three byte formatters over the same rows**, producing three different strings for one file
  (`512 B` vs `0.5 KB`); five app-wide.
- **The brand colour hardcoded five times in `SharedDocument.razor`** — the public share page, in a
  file that uses tokens for its fonts. This is the class CLAUDE.md calls the theme audit's "most
  substantive find". A rebrand leaves the page people outside the school see on the old wine.
- Smaller: the 25 MB limit in four constants and four strings; the `"t"` query key as four literals;
  `"dd MMM yyyy"` as four API-side literals; the permission display strings duplicated between the
  seeder and the constants; `PolicyEdit` and `ShareForm` shadowing their shared DTOs by hand.

---

## 14. What the post-build audit found (Revision 3, 2026-09-18)

Ranked by consequence. Findings 1–3 are defects against this plan's own stated intent; 4–6 are
decisions nobody has taken.

1. **Deleting a document destroys its share audit trail, and the person doing it need not be allowed
   to read that trail.** `DELETE media/{mediaId}` is gated on `content.delete`, which the seeded
   Manager role holds. `document_shares → MediaContent` and `document_share_events → DocumentShare`
   are both `DeleteBehavior.Cascade`. So one delete erases every record of who opened that document,
   performed by someone who may hold neither `documents.share.manage` nor `documents.share.audit`.
   This contradicts the controller's own words — *"Revokes. Never deletes: the history of who could
   open a document is the audit answer"* — and **§8's retention matrix, which promises the event row
   survives attribution blanking.** It is also the single control NIST AU-9 exists to impose:
   protect audit information "from unauthorized access, modification, and **deletion**".
   *Fix:* restrict the delete, or soft-delete the document and keep the shares, or re-parent the
   events. A decision is needed; silently cascading is not one of the options.
2. **The signage/share leak** — see the correction in §1. A shareable document on a playlist is
   served anonymously. Never tested, because §12's list was written as two exclusive cases.
3. **"Publish to Library" appears on tenants that cannot use it.** All seven print routes gate the
   control on `library.publish` alone and never on the module, while the endpoint is
   `[RequireModule(EngagementCommunications)]`. The user pays for a CDN fetch and a full-page raster
   before being told. CLAUDE.md's own rule: *an absent button reads as an absent permission.* The
   Document Library page itself also lacks the module guard every sibling content page has, and is
   the navigation target of every successful publish.
4. **There is no classification on a document, so the policy is one level coarser than the
   comparators'.** Microsoft sets the default sharing scope and block-download *per sensitivity
   label*, resolving conflicts by "the more restrictive"; Google applies labels by DLP rule and locks
   them against downgrade. Here, a signed appraisal and a car-park notice get the same tenant-wide
   link cap, the same retention and the same download default — and the only signal that a document
   concerns a named member of staff is free text in `Summary`, which no decision reads.
   **If a classification is added, add the lock at the same time, or it is decoration.**
5. **One retention clock where the statute contemplates several.** `AttributionRetentionDays` is
   tenant-wide. Purview treats sensitivity and retention as two separate labels on one item and
   permits up to 50 differentiated audit-retention policies. Uganda s.18(1)(a)–(b) explicitly
   contemplates retention "required or authorised by law" for some records and not others — which is
   precisely "keep safeguarding-document access records for years, marketing ones for ninety days",
   and cannot be expressed today. Related: the viewer's email also sits in the notification row for
   `InAppRetentionDays`, and `NotificationLog` stores its recipient in clear with **no purge job at
   all**, so the shortest configured window is not the effective one.
6. **Gaps worth closing cheaply, each with a precedent:**
   - **Device-binding on email verification.** DocSend: "the link must be clicked from the same
     device that the visitor opened the document with", and a verified viewer stays verified two
     weeks. This product verifies the address but not the device, so the code is forwardable.
   - **Warn before a link expires.** Box notifies item owners a configurable period ahead, default
     7 days. A link that dies silently generates a support call.
   - **Referrer hygiene on the share page.** The W3C TAG finding names the `Referer` header, server
     logs, browser history and history-syncing as leak routes for a capability URL. Worth checking
     whether this product's own request logging records the slug.
   - **Record *why*, not only who.** ISO 23081-1, as quoted in MoReq2010, expects an event history to
     say "why it occurred". Revocation already takes a reason; share *creation* does not.
   - **Antivirus scanning before serving** is ASVS 5.4.3. The standing no-server-dependencies rule
     makes this a consciously accepted risk — **state it as accepted rather than omit it**, since a
     reviewer working from ASVS will look for it.

### The internal-circulation case was never in this plan's scope

§9's worked example is *"minutes to staff who have **no login**"* — the external path. Circulating a
document to staff who **do** have logins is a different problem, and it has no delivery today:
attaching minutes to a meeting writes an activity row and notifies nobody, and the portal's
"Coming up" lists duties "not yet over", so the meeting leaves the attendee's portal before the
minutes arrive. The design note **"Circulating the Minutes"** sets out the recommendation — one
nullable `StaffNotice.AudienceDutyId`, and "Attach minutes" offering to circulate — which keeps the
Library as the store, the duty as the audience and the notice as the channel, with no second copy of
the file and no second notification path.

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

### Added in Revision 3 (2026-09-18) — primary sources

**Law governing these tenants**
- [Uganda, Data Protection and Privacy Act 2019 (Act 9 of 2019, now Cap. 97)](https://ulii.org/akn/ug/act/2019/9/eng@2019-05-03) ([PDF](https://media.ulii.org/media/legislation/18002/source_file/b6ae5cce4290322a/2019-9.pdf)) — s.3(1) principles; s.14 minimality; **s.18(1) retention, s.18(4) destroy/delete/de-identify at expiry, s.18(5) irreversibly**; s.20 security measures; **s.24(1)(c) the subject's right to be told who has had access**
- [Uganda, Data Protection and Privacy Regulations 2021 (S.I. No. 21 of 2021)](https://ulii.org/akn/ug/act/si/2021/21/eng@2021-03-12) — reg. 31 technical measures restricting access; **reg. 39(7) notify third parties the data was disclosed to**
- [GDPR Article 5](https://gdpr-info.eu/art-5-gdpr/) — minimisation, storage limitation, accountability. Uganda s.3(1)(c) tracks Art. 5(1)(c) closely; s.18(4)–(5) is *more* explicit than GDPR about expiry

**Standards**
- [NIST SP 800-53 Rev 5](https://doi.org/10.6028/NIST.SP.800-53r5) (control texts via the [csf.tools mirror](https://csf.tools/reference/nist-sp-800-53/r5/)) — **AC-3** access enforcement; **AU-3** content of audit records; **AU-3(3)** limit PII in audit records (the control that licenses IP truncation); **AU-9** protection of audit information from modification and deletion; **AU-11** organization-defined retention
- [ISO 15489-1:2016](https://www.iso.org/standard/62542.html) ([free preview](https://cdn.standards.iteh.ai/samples/62542/fe383f4fe10448d5b22ce628b1542ed6/ISO-15489-1-2016.pdf)) — cl. 5.2.1 authenticity, reliability, integrity, useability as the test of authoritative evidence; 5.2.2.1 authenticity; 5.2.2.2 records created at the time of the event
- MoReq2010 — "makes no provision for the copying of records", because a copy "loses part of its event history and does not have parity with the original"; quotes ISO 23081-1 on an event history recording **why** it occurred
- [OWASP ASVS 5.0](https://github.com/OWASP/ASVS/tree/master/5.0/en) — **V5** file handling (5.2.2 extension/content match, 5.3.1 no execution, 5.3.2 internally generated paths, 5.4.2 RFC 6266, **5.4.3 antivirus before serving**); **V8** authorization (8.2.2 object-level access, 8.3.1 trusted service layer, **8.3.2 rule changes apply immediately**, 8.4.1 cross-tenant)
- [OWASP Authorization Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html) — deny by default; validate on every request; **a guessable identifier is not access control**
- [W3C TAG Finding — Good Practices for Capability URLs (30 Oct 2014)](https://www.w3.org/2001/tag/doc/capability-urls/) — "**120 bits or more**" of entropy from a secure RNG; capability URLs "should expire"; revocation and **multiple links per resource**; HTTPS; leak routes (Referer, logs, history, shorteners). A TAG *finding*, not a Recommendation — no normative force. It recommends neither a second factor nor an audit log, so this product's passcode/email/event design goes beyond it

**Vendor documentation (primary)**
- [SharePoint/OneDrive shareable links](https://learn.microsoft.com/en-us/sharepoint/shareable-links-anyone-specific-people-organization) — a link as "a transferable, revocable secret key"; expiry affects the link, not the file; **"People using an Anyone link don't have to authenticate, and their access can't be audited"**
- [Purview audit retention](https://learn.microsoft.com/en-us/purview/audit-log-retention-policies) — 180-day default, 1 year on E5, up to 10 years by add-on · [block download](https://learn.microsoft.com/en-us/sharepoint/block-download-from-sites) (requires SharePoint Advanced Management)
- [Purview sensitivity labels](https://learn.microsoft.com/en-us/purview/sensitivity-labels) and [label-driven default sharing link](https://learn.microsoft.com/en-us/purview/sensitivity-labels-default-sharing-link) — labels are "persistent" and in "clear text"; `DefaultSharingScope` per label; **"the more restrictive scope settings will be applied"**
- [Google Drive sharing](https://support.google.com/drive/answer/2494822) · [publish to web](https://support.google.com/docs/answer/183965) · [Drive log events](https://knowledge.workspace.google.com/admin/reports/drive-log-events) · [log retention — 6 months](https://knowledge.workspace.google.com/admin/reports/data-retention-and-lag-times) — external actions "shown as **anonymous** except when the item is explicitly shared with them" · [DLP-applied labels locked against downgrade](https://knowledge.workspace.google.com/admin/security/apply-classification-labels-to-drive-files-automatically-with-dlp-rules)
- [Box shared-link settings](https://support.box.com/hc/en-us/articles/360043697554-Configuring-Individual-Shared-Link-Settings) · [enterprise defaults](https://support.box.com/hc/en-us/articles/4404822772755-Enterprise-Settings-Content-Sharing-Tab) (auto-disable default 60 days; notify owners before expiry, default 7) · [watermarking](https://support.box.com/hc/en-us/articles/360044195253-Watermarking-Files) — per-viewer identity and time of access, with no capture-prevention claim
- [Dropbox link permissions](https://help.dropbox.com/share/set-link-permissions) — disabling downloads "doesn't prevent people from saving the content using other methods"
- [DocSend email authentication](https://help.dropbox.com/share/dropbox-docsend-email-authentication) — verification link valid one hour, viewer stays verified two weeks, and **"the link must be clicked from the same device"** · [time-per-page measurement](https://help.docsend.com/hc/en-us/articles/205639127-How-do-you-measure-a-visitor-s-time-spent-on-my-document)
- School MIS: [Bromcom published documents](https://docs.bromcom.com/knowledge-base/how-to-manage-published-documents/) (publication is a flag; push notification an opt-in tickbox) · [Arbor report cards](https://support.arbor-education.com/hc/en-us/articles/360012041914-Send-Arbor-Report-Cards-to-parents-or-students) and [parental consents](https://support.arbor-education.com/hc/en-us/articles/203813172-See-add-send-out-or-update-Parental-Consents) — portal publication "does not send a push notification, email or SMS"

**Deliberately not cited** (checked and unverifiable at the time of writing): ISO 15489-1 cl. 5.2.2.3–5.2.2.4 and any ISO 16175-1 clause (paywalled); whether a Google "Anyone with the link" link can be given an expiry; SharePoint's reported 7-day minimum internal-link expiry; Box classification-driven link expiry; DocSend's exact watermark field list and analytics retention; Diligent read-receipt specifics; Box/Dropbox audit retention; SIMS.
