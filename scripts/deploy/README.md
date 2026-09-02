# Q-Mgr — Linux deployment

Mirrors `E:\ERP\scripts\deploy\` (same server, same owner, same operational shape) — read
that repo's README first if this is unfamiliar territory. This doc only covers what's
different for Q-Mgr.

## What's different from ERP's deploy scripts

| | ERP | Q-Mgr |
|---|---|---|
| Deployable processes | one monolith | **two**: `Q-Mgr.API` + `Q-Mgr.Web` (Blazor Server) |
| Hostname | wildcard `*.cashbook.ug` (SaaS, one tenant per subdomain) | single host `qmgr.cashbook.ug`, **path-based** routing |
| Routing | subdomain -> tenant | `/` -> Web (8582 default), `/api/` + `/hubs/` + `/openapi/` -> API (8581 default) |
| TLS cert | same wildcard cert, `/etc/ssl/certs/cashbook.ug.crt` | **same cert, same paths** — shared box, shared cert |
| Tenancy | Single-DB or database-per-tenant (`.lic`-gated Multi mode) | shared-schema, one DB, `OrganizationId` per row — no catalog DB |
| DB creation | manual catalog-DB step before first start | **automatic** — `DatabaseInitializer` creates the DB and runs migrations + RBAC/SuperAdmin/demo seeding on first boot |
| systemd units | 1 | 2 (`qmgr-api.service`, `qmgr-web.service`) |

## Files

- `Common.ps1` — shared helpers (clean, version stamping, publish-with-retry, static-asset
  pre-compression, publish-noise stripping, manifest writer). Same function names/behavior
  as ERP's `Common.ps1`; `Invoke-CleanArtefacts` takes a list of project dirs here since
  there are two projects to clean, not one.
- `build-linux.ps1` — run **locally** (Windows dev machine) to produce the deployment
  package. Publishes both projects self-contained for `linux-x64`, generates the nginx
  site, both systemd units, `install.sh`, and packages everything into
  `qmgr-<version>-<date>.tar.gz` under `dist/`.
- `dist/` — build output (gitignored). Not checked in.

## Package layout (what's inside the .tar.gz)

```
install.sh              <- top level, same convention as ERP's and CRM's own packages
deploy-manifest.json
api/                     Q-Mgr.API self-contained publish
web/                     Q-Mgr.Web self-contained publish
config/
  qmgr-api.service
  qmgr-web.service
  qmgr.nginx.conf
```

`install.sh` deliberately sits at the **package root**, not one level down. Both ERP's and
CRM's own deploy docs teach `cd /tmp && tar -xzf <pkg> && sudo bash install.sh` — that exact
command only works if `install.sh` is at the top of the archive. An earlier version of this
script nested it under `server/`, which meant that command silently ran a **stale `install.sh`
left over from a previous ERP deploy** in the same `/tmp` instead of erroring, and Q-Mgr was
never actually installed. Fixed by flattening the layout to match.

## Before building: confirm the ports are actually free on the target server

**Do this every time, not just on the first deploy.** `qmgr.cashbook.ug` shares its box with a
growing list of unrelated apps (ERP, CashBook, evolweb, evol-api, evol-ui, docmgr, `must`,
maryhill, ...), each independently claiming a port in the 8500s/8580s with no central registry.
The default ports below (`-ApiPort 8581 -WebPort 8582`) are just fallback values baked into the
script — they are **not** a promise either is free on any given server. The first real deploy to
`74.208.201.32` hit exactly this twice in a row (8581 was already CashBook's, then 8582 was
already evolweb's) before landing on `8586`/`8587`.

Check before every build/deploy, not just the first one — other apps on the box can claim new
ports at any time:

```bash
ss -tlnp        # full picture: every listening port on the box, not just .NET ones
```

Pick two **free, consecutive** ports (API first, Web = API+1 — kept in sequence on purpose so
the pair reads as one unit rather than two arbitrary numbers to remember separately) and pass
them explicitly.

**Safety net, not a substitute for the check above**: `install.sh` now refuses to proceed if the
ports baked into the build it's installing don't match whatever ports are already live in the
server's existing `qmgr-api.service`/`qmgr-web.service` units (skipped automatically on a genuine
first install, where nothing is live yet to compare against). This is exactly the mistake that bit
the very first upgrade after this guard was added — a build made with the script's bare defaults
(8581/8582) instead of this box's actual assigned ports (8586/8587) would otherwise have silently
overwritten a working install with the wrong ones and crash-looped `qmgr-web`. If you ever see:

```
==> REFUSING to proceed: this build's ports don't match what's already live on this server.
    Currently live : API=8586  Web=8587
    This build     : API=8581  Web=8582
```

either rebuild with `-ApiPort`/`-WebPort` matching what's already live (the normal fix — this
almost always means the build was made without checking what this specific server already runs),
or, if the port change is genuinely deliberate, confirm the new ports are actually free with
`ss -tlnp` and re-run with `sudo bash install.sh --force-ports`.

## Building a release

```powershell
cd scripts/deploy
./build-linux.ps1 -PgPassword 'the-production-postgres-password' -ApiPort 8586 -WebPort 8587
```

Useful overrides (see the `param()` block in `build-linux.ps1` for the full list):

```powershell
./build-linux.ps1 `
  -PgPassword 'REDACTED' `
  -PgUser 'qmgr_app' `
  -PgDatabase 'qmgr' `
  -HostName 'qmgr.cashbook.ug' `
  -ApiPort 8586 -WebPort 8587 `
  -InstallRoot '/var/www/sites/qmgr' `
  -UploadsPath '/var/www/uploads/qmgr'
```

`-JwtSecret` is optional — if omitted, a fresh random 64-byte secret is generated per
build and baked into that build's `appsettings.Production.json`. **On an upgrade (not a
first install), `install.sh` preserves the server's existing
`appsettings.Production.json`**, so a freshly-generated secret from a later build never
silently invalidates every live session — the packaged file only wins on a genuine first
install.

Version comes from `../../version.json` (repo root) plus a `yyyyMMdd.HHmm.<githash>`
build stamp — same format as ERP's `Get-RepoBuildVersion`. Bump `version.json` before
cutting a release the same way you would in ERP.

## Deploying

```bash
scp dist/qmgr-<version>-<date>.tar.gz root@server:/tmp/
ssh root@server
mkdir -p /tmp/qmgr-deploy && tar -xzf /tmp/qmgr-<version>-<date>.tar.gz -C /tmp/qmgr-deploy
cd /tmp/qmgr-deploy
sudo bash install.sh
```

Use a dedicated subdirectory (`/tmp/qmgr-deploy` above) rather than extracting straight into
`/tmp` if `/tmp` might already hold files from another deploy (ERP, an earlier Q-Mgr build) —
that's exactly the collision that bit the very first install attempt. Extracting into its own
empty directory means there's nothing already there for `install.sh` (or anything else) to
collide with.

`install.sh` is idempotent and safe to re-run for upgrades: stop services -> backup
current install (keep last 3 complete backups) -> rsync new build in (excluding the
runtime-writable uploads dir) -> restore the server's own `appsettings.Production.json`
-> fix permissions -> install/reload systemd units and nginx -> start -> print status.

**First install only**: the packaged `appsettings.Production.json` (with the DB
credentials and JWT secret from the build) is used as-is since there's nothing to
restore. Review it once on the server (`/var/www/sites/qmgr/api/appsettings.Production.json`)
before considering the install final — rotate the JWT secret if the build machine's
generated one shouldn't be trusted long-term.

**No manual database step.** Unlike ERP, there is nothing to run before starting
`qmgr-api.service` — it creates the `qmgr` database if missing and applies every EF Core
migration plus RBAC/SuperAdmin/demo seeding automatically on first boot. Confirm the
Postgres role in the connection string has `CREATEDB` for a true first install against a
brand-new Postgres instance; after that first boot it no longer needs that privilege.

## Runtime layout on the server

```
/var/www/sites/qmgr/
  api/            Q-Mgr.API self-contained publish (systemd: qmgr-api.service, :8582 loopback)
    wwwroot/uploads -> symlink to /var/www/uploads/qmgr   (persists across upgrades)
  web/            Q-Mgr.Web self-contained publish (systemd: qmgr-web.service, :8581 loopback)
/var/www/uploads/qmgr/   media uploads — outside the deploy tree on purpose, survives every rsync --delete
/etc/nginx/sites-available/qmgr.conf   path-based routing, wildcard cert shared with ERP
/etc/systemd/system/qmgr-api.service
/etc/systemd/system/qmgr-web.service
/var/log/qmgr/
```

Neither service binds a public port — nginx is the only thing exposed on 80/443, proxying
to both on loopback. Health check reachable at `https://qmgr.cashbook.ug/api-health`.
