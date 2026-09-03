# Q-Mgr API Integration Guide

How a third-party system (a hospital's HIS, a bank's core system, a pharmacy system, or any
other external application) sends data into Q-Mgr and reads it back. Written 2026-08-25, current
as of that date's codebase — the endpoint reference below was generated directly from the live
OpenAPI spec (`GET /swagger/v1/swagger.json` against a running instance), not hand-typed, so it
reflects what's actually implemented rather than what was originally planned.

## Two ways to integrate

1. **Call the REST API directly** with an API key. This is the primary path — same HTTP API the
   admin web app itself uses, authenticated differently.
2. **Embed the `Q-Mgr.IntegrationSdk` C# client library** (`src/Q-Mgr.IntegrationSdk/`) directly in
   your own .NET application. It's a thin, typed wrapper around option 1 — `HospitalManagementAdapter`,
   `BankingSystemAdapter`, and `PharmacySystemAdapter` model the domain-specific flow (patient
   check-in, banking queue enrollment, etc.) and call `QueueIntegrationClient`, which does the same
   HTTP calls described below. Build it standalone: `dotnet build src/Q-Mgr.IntegrationSdk`. It has
   zero reference to the rest of this repo, so it can be packaged and handed to a partner on its own.

Both paths end up at the same authenticated REST API — the SDK is convenience, not a different
mechanism.

## Authentication

Q-Mgr's API accepts two independent auth mechanisms on the same endpoints:

| | Staff / admin (JWT) | External system (API key) |
|---|---|---|
| Used by | The Q-Mgr web app, logged-in humans | Partner systems calling in programmatically |
| How | `Authorization: Bearer <token>` from `POST /api/v1/auth/login` | `X-API-Key` + `X-API-Secret` headers (client id **and** secret — see below) |
| Identity | A real `User` row, full role-based permissions | An `ApiClient` row, permissions limited to its configured scopes |
| Issued from | Login form | `/admin/api-clients` (or `POST /api/v1/api-clients`) |

### Getting an API key

1. In the Q-Mgr admin app, go to **Integrations → API Clients** (`/admin/api-clients`), or call
   `POST /api/v1/api-clients` as an authenticated admin.
2. Give it a name, a `systemType` (`hospital_mgmt`, `banking`, `pharmacy`, or anything else —
   informational only), and select **scopes** — see the table below for what each one unlocks.
3. The response includes `clientId` (`qmgr_` + 16 hex characters) and `clientSecret` (`qmgr_sk_` +
   48 hex characters, **shown exactly once** — only a BCrypt hash is stored; use
   `POST /api/v1/api-clients/{id}/regenerate-secret` if it's lost, which invalidates the old one
   immediately).
4. Send **both** the client id and the secret on every request, in either of these two equivalent
   forms:

   ```
   # Form A — two headers
   X-API-Key: qmgr_xxxxxxxxxxxxxxxx
   X-API-Secret: qmgr_sk_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx

   # Form B — one header, client id and secret joined by a single '.'
   X-API-Key: qmgr_xxxxxxxxxxxxxxxx.qmgr_sk_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
   ```

   Form B is split on the **first** `.` (client ids and secrets are hex-only and can never contain
   one). If `X-API-Key` contains a `.`, `X-API-Secret` is ignored.

**The secret is mandatory.** Since 2026-09-03 the client id alone is no longer a credential — a
request carrying `X-API-Key` without a secret (in either form) is rejected with `401` and a
`missing_api_secret` body that restates the header contract; a wrong secret or unknown/inactive
client id gets the same `401 invalid_api_key` response (deliberately indistinguishable). Successful
verifications are cached server-side for 10 minutes so the BCrypt check isn't paid on every call;
deactivating a client (`isActive: false`) takes effect on the very next request regardless, and
regenerating the secret invalidates the cache immediately.

Alternatively, exchange the pair for a short-lived JWT via the OAuth2 client-credentials flow at
`POST /api/v1/auth/token` (`{ "clientId": "...", "clientSecret": "..." }`) and send
`Authorization: Bearer <token>` instead — same identity, same scopes, same endpoint rules.

### Scopes → what they actually unlock

Each scope maps to one or more of Q-Mgr's real permission codes (the mapping lives in
`src/Q-Mgr.API/Authorization/PermissionAuthorizationHandler.cs`; the canonical list is
`PermissionAuthorizationHandler.AllScopes`). A request without the right scope gets a normal
`403 Forbidden`, not a silent no-op. A scope name not in this table grants nothing.

| Scope | Permission codes | Grants |
|---|---|---|
| `queue:read` | `queue.view` | View queue status (`GET /api/v1/branches/{id}/queue/status`) |
| `queue:write` | `queue.manage` | Call-next/complete/transfer operations on counters |
| `token:read` | `tokens.view` | Read tokens (get by id / by external reference / by customer, list waiting) |
| `token:create` | `tokens.create` | Create new tokens (`POST /api/v1/branches/{id}/tokens`) — the main "push a customer/patient into the queue" endpoint |
| `token:manage` | `queue.manage`, `tokens.cancel` | Cancel tokens, plus the queue:write operations above |
| `counter:read` | `counters.view` | View counter list/status |
| `service:read` | `service-types.view` | View service types |
| `stats:read` | `reports.view` | View reports/analytics |
| `roster:read` | `students.view` | View the student/guardian visitor roster |
| `roster:write` | `students.manage` | Create/update/delete students and guardians, bulk roster import (e.g. from a school MIS) |
| `content:read` | `content.view` | View media, playlists, displays (digital signage) |
| `content:write` | `content.create`, `content.edit` | Upload media, create/edit playlists and schedules |
| `settings:write` | `settings.edit` | Edit organization/branch settings (e.g. the branch display ticker banner) |
| `visitors:read` | `visitors.view` | View the visitor log and visitor details |
| `visitors:write` | `visitors.manage`, `visitors.checkin`, `visitors.checkout` | Pre-register, check in/out, edit and delete visitors, manage the watchlist |
| `marketing:read` | `marketing.view` | View marketing contacts and broadcast campaigns |
| `marketing:send` | `marketing.manage`, `marketing.send` | Manage contacts, create/edit broadcast drafts, schedule/send broadcasts |
| `welfare:read` | `welfare.view` | View non-confidential Student Welfare Ledger records |
| `welfare:write` | `welfare.create`, `welfare.edit` | Create welfare records and append follow-up notes |

Not exposed to API keys by design (JWT/staff only): confidential welfare records
(`welfare.confidential.view`), guardian notifications (`welfare.notify`), welfare categories/reports,
user/role/branch administration, billing, API-client management itself, and all platform-admin
surfaces.

### Which endpoints accept an API key at all

An API key can only call endpoints that are explicitly guarded by a permission (a
`[RequirePermission(...)]` on the action or controller) — that's what the scope table above maps
onto. Endpoints that merely require a signed-in user (`[Authorize]` with no specific permission)
reject API-key principals with `403 { "error": "API_KEY_NOT_ALLOWED" }` even if the key is valid.
Public `[AllowAnonymous]` endpoints (feedback submission, display/kiosk reads, `/health`) still work
with or without a key. If a partner needs an endpoint that today only carries `[Authorize]`, the fix
is to give that endpoint a real permission and map a scope to it — not to loosen this rule.

### Tenant & branch resolution

Every meaningful endpoint is scoped by `branchId` in the URL path
(`/api/v1/branches/{branchId}/...`). The API key's `OrganizationId` is checked against that
branch's actual organization on every call — you cannot use a valid key from one tenant to reach
another tenant's branch, even by guessing a `branchId`.

## Example: a hospital checking in a patient

```bash
# 1. Create a token (patient joins the queue)
curl -X POST https://your-qmgr-instance/api/v1/branches/{branchId}/tokens \
  -H "X-API-Key: qmgr_xxxxxxxxxxxxxxxx" \
  -H "X-API-Secret: qmgr_sk_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" \
  -H "Content-Type: application/json" \
  -d '{
    "serviceTypeCode": "GEN",
    "customer": { "name": "Jane Doe", "phone": "+15550001234" },
    "priority": 0,
    "externalReference": "HIS-APPT-4821",
    "externalSystem": "acme_his"
  }'
# -> 201, returns { "id": "...", "displayNumber": "G004", "positionInQueue": 3, ... }

# 2. Poll queue position (or look it up by your own external reference)
curl "https://your-qmgr-instance/api/v1/branches/{branchId}/tokens/by-reference?externalSystem=acme_his&externalReference=HIS-APPT-4821" \
  -H "X-API-Key: qmgr_xxxxxxxxxxxxxxxx" \
  -H "X-API-Secret: qmgr_sk_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"

# 3. Read live queue status for the whole branch (combined single-header form, same thing)
curl https://your-qmgr-instance/api/v1/branches/{branchId}/queue/status \
  -H "X-API-Key: qmgr_xxxxxxxxxxxxxxxx.qmgr_sk_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
```

The `HospitalManagementAdapter` in the SDK wraps step 1 as `CheckInPatientAsync(...)`, mapping
hospital department names to Q-Mgr service type codes and setting priority automatically for
emergency/VIP/senior patients — same HTTP call underneath.

## Full endpoint reference

The complete, current list of every endpoint (208 operations across 29 areas — queue, tokens,
visitors, marketing/broadcasts, billing, content/signage, users/roles, platform admin, etc.) is in
the accompanying **Postman collection**, generated directly from the live OpenAPI spec rather than
hand-maintained, so it won't drift from the real code:

- `postman/Q-Mgr-API.postman_collection.json` — the collection itself (208 requests, one folder per controller)
- `postman/Q-Mgr-Local.postman_environment.json` — a starter environment (`baseUrl` pointed at `https://localhost:5001`, empty `accessToken`/`apiKey` slots)

Import both into Postman, select the "Q-Mgr Local" environment, then either run the
**Auth → Login** request (its test script auto-populates `accessToken` from the response) or set
`apiKey` yourself for the external-integration endpoints above — every request is ready to send
with a realistic example body already filled in.

To regenerate the collection after future API changes: fetch the live spec (`curl -k
https://localhost:5001/swagger/v1/swagger.json`) and re-run `scripts/convert-postman.ps1` against
it — it resolves every schema reference into a real example body rather than emitting bare stubs.

You can also browse the API live while it's running:
- Swagger UI: `https://localhost:5001/swagger`
- Scalar (modern alternative UI): `https://localhost:5001/scalar/v1`
- Raw OpenAPI JSON: `https://localhost:5001/swagger/v1/swagger.json` or `/openapi/v1.json`

The areas most relevant to third-party integration specifically:

| Area | Endpoints |
|---|---|
| **Tokens** | Create, get, get-by-external-reference, get-by-customer, cancel, list waiting |
| **Queue** | Branch queue status (counts, wait times, per-service breakdown) |
| **Counters** | Call-next, call-specific, complete, no-show, transfer (JWT/staff only today) |
| **Feedback** | Submit feedback for a token (public, `[AllowAnonymous]`) |

## Errors

Standard ASP.NET Core `ProblemDetails` shape on failure:

```json
{ "title": "Branch not found", "detail": "Branch with ID '...' was not found in your organization.", "status": 404 }
```

`401` = auth failed entirely (bad/missing key or token). `403` = authenticated but missing the
required scope/permission. `404` on a branch-scoped resource can also mean "exists, but not in your
organization" — deliberately indistinguishable from "doesn't exist" to avoid leaking cross-tenant
existence.

API-key-specific responses (these come from the auth middleware, so they use a flat
`{ "error": ... }` shape rather than `ProblemDetails`):

| Status | Body | Meaning |
|---|---|---|
| `401` | `{ "error": "missing_api_secret", "error_description": "...", "headers": {...} }` | `X-API-Key` was sent without a secret — restates both header forms |
| `401` | `{ "error": "invalid_api_key", "error_description": "..." }` | Unknown client id, inactive client, or wrong secret |
| `403` | `{ "error": "API_KEY_NOT_ALLOWED", "message": "..." }` | Valid key, but the endpoint is not permission-guarded (signed-in users only) |
| `403` | standard `ProblemDetails` | Valid key, permission-guarded endpoint, but none of the key's scopes grant it |
| `429` | `{ "error": "RATE_LIMITED", "limit": 100, "retryAfterSeconds": 23 }` + `Retry-After` header | Per-client `rateLimitPerMinute` exceeded in the current one-minute window |

### Rate limiting

Every API client has its own `rateLimitPerMinute` (default 100, visible on the client in the admin
UI). It is enforced as a fixed one-minute window (UTC clock minutes, i.e. the counter resets at
`:00` seconds) counted per client id across all endpoints; only successfully authenticated requests
count, so a third party who knows your client id but not your secret cannot exhaust your quota. When
exceeded you get `429` with a `Retry-After` header (seconds until the window rolls over) and the
JSON body shown above — back off for that many seconds rather than retrying immediately. A value of
`0` disables the per-client limit. The global IP-based rate limit (`IpRateLimiting` in
`appsettings.json`) still applies on top, independently.

## Known limitations

- **The per-client rate limit counter and the verified-secret cache are in-process memory**
  (`IMemoryCache`). Running more than one API instance behind a load balancer multiplies the
  effective limit by the instance count and means a regenerated secret is re-verified per instance
  — fine for a single deployment, worth moving to a shared store before scaling out.
- **`rateLimitPerMinute` cannot yet be changed from the admin UI or `PUT /api/v1/api-clients/{id}`** —
  it's created at the default (100) and only adjustable directly in the database for now.

## Webhooks

> **.NET integrators:** the `QMgr.IntegrationSdk` package ships `QMgrWebhookVerifier`, which does
> the signing and the constant-time verification described below. Prefer it over reimplementing
> the HMAC: comparing hex strings with `==` leaks timing, and re-serializing the JSON before
> hashing changes the very bytes the signature covers.

Webhooks are configured per API client on **Admin → API Clients** (`/admin/api-clients`), and
require the `integrations-api` module to be active for the organization. Each client has:

| Field | Purpose |
|---|---|
| `webhookUrl` | Where Q-Mgr POSTs outbound events. Must be absolute `https://` (`http://` is only accepted for localhost). |
| `webhookEvents` | Which outbound events to deliver (subset of the list below). |
| Webhook secret | A 32-byte random key (Base64). **Shared for both directions**: Q-Mgr signs outbound deliveries with it, and you sign inbound calls with it. Shown once when created/rotated — `GET` never returns it, only `hasWebhookSecret: true`. |

Manage it via the API client endpoints (`Authorization: Bearer <JWT>`, `api-clients:*` permissions):

```
GET  /api/v1/api-clients/webhook-events                # canonical catalog (outbound + inbound), static
POST /api/v1/api-clients                               # body may include webhookUrl + webhookEvents
PUT  /api/v1/api-clients/{id}                          # same; response carries webhookSecret ONLY if one was just generated
POST /api/v1/api-clients/{id}/webhook-secret/rotate    # new secret, returned once in `webhookSecret`
```

A secret is generated automatically the first time a `webhookUrl` is saved. For an inbound-only
integration (no outbound URL) call `.../webhook-secret/rotate` once to create one.

### Outbound events (Q-Mgr → you)

| Event | Fires when |
|---|---|
| `token.created` | A token joined the queue (kiosk, web, API, or the inbound webhook below) |
| `token.called` | A token was called to a counter |
| `token.serving` | Service started at the counter |
| `token.completed` | Service finished |
| `token.cancelled` | Token cancelled (staff, customer, or integration) |
| `token.no_show` | A called token did not show up |

Delivery is a `POST` to `webhookUrl` with `Content-Type: application/json` and two headers:

```
X-QMgr-Event: token.called
X-QMgr-Signature: sha256=<hex HMAC-SHA256 of the raw request body, keyed with the webhook secret>
```

Body:

```json
{
  "event": "token.called",
  "timestamp": "2026-09-03T08:15:42.117Z",
  "data": {
    "token": {
      "id": "0f6b...", "display_number": "G004", "status": "called",
      "counter": { "id": "b1c2...", "number": 3 },
      "customer": { "id": "MRN-88213", "name": "Jane Doe" },
      "external_reference": "APT-2026-000917",
      "wait_time_minutes": 12
    },
    "branch": { "id": "7a9e..." }
  }
}
```

Respond with any 2xx within the request timeout. Non-2xx or a network error is retried by the
minutely delivery job, up to 5 attempts, after which the delivery is marked `failed`. Deliveries
are queued only while the organization's `integrations-api` module is active; if it lapses, the
configuration is kept but nothing is sent until it's active again. Clients with `allowedBranches`
set only receive events for those branches.

**Verify the signature** over the *raw* body bytes (before JSON parsing), constant-time compare:

```csharp
// ASP.NET Core minimal API
app.MapPost("/qmgr/webhook", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var body = ms.ToArray();

    var header = req.Headers["X-QMgr-Signature"].ToString();            // "sha256=<hex>"
    if (!header.StartsWith("sha256=")) return Results.Unauthorized();
    var provided = Convert.FromHexString(header["sha256=".Length..]);

    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WEBHOOK_SECRET));
    var expected = hmac.ComputeHash(body);
    if (provided.Length != expected.Length ||
        !CryptographicOperations.FixedTimeEquals(provided, expected))
        return Results.Unauthorized();

    var evt = req.Headers["X-QMgr-Event"].ToString();
    // ... handle evt / JsonDocument.Parse(body)
    return Results.Ok();
});
```

```js
// Node (Express with express.raw({ type: 'application/json' }))
const crypto = require('crypto');
app.post('/qmgr/webhook', (req, res) => {
  const header = req.get('X-QMgr-Signature') || '';
  const provided = Buffer.from(header.replace(/^sha256=/i, ''), 'hex');
  const expected = crypto.createHmac('sha256', WEBHOOK_SECRET).update(req.body).digest();
  if (provided.length !== expected.length || !crypto.timingSafeEqual(provided, expected))
    return res.sendStatus(401);
  const event = JSON.parse(req.body);
  res.sendStatus(200);
});
```

### Inbound events (you → Q-Mgr)

Each API client has its own receiver URL, shown on its card in the admin UI:

```
POST {ApiPublicUrl}/api/v1/webhooks/inbound/{apiClientId}
Content-Type: application/json
X-QMgr-Signature: sha256=<hex HMAC-SHA256 of the raw body, keyed with the same webhook secret>
```

No JWT or API key — the signature is the authentication. The client must be active and have a
webhook secret; otherwise (or on a bad/missing signature) the response is a bare `401`. The body
is capped at 64 KB (`413` above that).

Envelope:

```json
{
  "event": "appointment.created",
  "branchId": "7a9e...",
  "externalReference": "APT-2026-000917",
  "data": { }
}
```

- `branchId` — required; must belong to your organization (and to the client's `allowedBranches`, if set).
- `externalReference` — your own id for the appointment; required for `appointment.cancelled`.
- `data` — event-specific, below.

| Event | `data` fields | Effect |
|---|---|---|
| `appointment.created` | `serviceTypeId` (guid, **required** — or `serviceTypeCode`), `customerId?`, `customerName?`, `customerPhone?`, `customerEmail?`, `priority?` (`normal`/`priority`/`vip`/`emergency` or 0-3), `estimatedArrival?` (ISO 8601) | Creates a token (`source = appointment`) with `externalReference` and `externalSystem = <client systemType, or "webhook">`. Idempotent: a live token with the same reference is returned as `already_exists` instead of creating a duplicate. |
| `appointment.cancelled` | none (uses top-level `externalReference`, **required**) | Cancels the token created for that reference in that branch, reason "Cancelled by integration". |

Note: tokens are matched on `(branchId, externalSystem, externalReference)`. `externalSystem` is
derived from the client's **System Type** — set it once and don't change it while references are
live, and if you also create tokens via `POST /api/v1/branches/{branchId}/tokens`, pass the same
`externalSystem` there for cancellations to find them.

Responses:

```
202 { "event": "appointment.created",   "tokenId": "0f6b...", "displayNumber": "G004", "status": "created" }
202 { "event": "appointment.created",   "tokenId": "0f6b...", "displayNumber": "G004", "status": "already_exists" }
202 { "event": "appointment.cancelled", "tokenId": "0f6b...", "displayNumber": "G004", "status": "cancelled" }
400 { "error": "UNSUPPORTED_EVENT", "supported": ["appointment.created", "appointment.cancelled"] }
400 { "error": "INVALID_JSON" | "MISSING_EVENT" | "MISSING_BRANCH" | "MISSING_SERVICE_TYPE" | "SERVICE_TYPE_NOT_FOUND" | "MISSING_EXTERNAL_REFERENCE" | "TOKEN_NOT_CREATED", "detail": "..." }
401 (no body)   -- unknown/inactive client, no secret configured, or bad signature
403 { "error": "MODULE_INACTIVE" | "BRANCH_NOT_ALLOWED" }
404 { "error": "BRANCH_NOT_FOUND" | "TOKEN_NOT_FOUND" }
409 { "error": "TOKEN_NOT_CANCELLABLE", "tokenId": "...", "status": "completed" }
413 { "error": "PAYLOAD_TOO_LARGE", "maxBytes": 65536 }
```

A token created this way also fires your own `token.created` outbound webhook (if subscribed),
so you can treat that as the delivery confirmation.

**curl example** — computes the HMAC with `openssl` and posts an appointment:

```bash
SECRET='<webhook secret from the admin UI>'
CLIENT_ID='<api client id (guid)>'
BODY='{"event":"appointment.created","branchId":"7a9e0c4e-1b8f-4f6a-9d2e-3c5b7a1e9f00","externalReference":"APT-2026-000917","data":{"serviceTypeId":"c1d2e3f4-5a6b-4c7d-8e9f-0a1b2c3d4e5f","customerName":"Jane Doe","customerPhone":"+256700000000","priority":"normal"}}'

SIG=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$SECRET" | sed 's/^.* //')

curl -i -X POST "https://qmgr.example.com/api/v1/webhooks/inbound/$CLIENT_ID" \
  -H "Content-Type: application/json" \
  -H "X-QMgr-Signature: sha256=$SIG" \
  --data-binary "$BODY"

# Cancel it later:
BODY='{"event":"appointment.cancelled","branchId":"7a9e0c4e-1b8f-4f6a-9d2e-3c5b7a1e9f00","externalReference":"APT-2026-000917"}'
SIG=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$SECRET" | sed 's/^.* //')
curl -i -X POST "https://qmgr.example.com/api/v1/webhooks/inbound/$CLIENT_ID" \
  -H "Content-Type: application/json" -H "X-QMgr-Signature: sha256=$SIG" --data-binary "$BODY"
```

Sign exactly the bytes you send (`--data-binary`, no re-serialization) — any whitespace or key
reordering between signing and sending produces a different digest and a `401`.
