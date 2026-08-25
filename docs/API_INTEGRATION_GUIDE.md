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
| How | `Authorization: Bearer <token>` from `POST /api/v1/auth/login` | `X-API-Key: <clientId>` header |
| Identity | A real `User` row, full role-based permissions | An `ApiClient` row, permissions limited to its configured scopes |
| Issued from | Login form | `/admin/api-clients` (or `POST /api/v1/api-clients`) |

### Getting an API key

1. In the Q-Mgr admin app, go to **Integrations → API Clients** (`/admin/api-clients`), or call
   `POST /api/v1/api-clients` as an authenticated admin.
2. Give it a name, a `systemType` (`hospital_mgmt`, `banking`, `pharmacy`, or anything else —
   informational only), and select **scopes** — see the table below for what each one unlocks.
3. The response includes `clientId` (this is the actual bearer value for `X-API-Key`) and
   `clientSecret` (shown once, used only by the separate OAuth2 client-credentials flow at
   `POST /api/v1/auth/token` if you'd rather exchange it for a short-lived JWT instead of sending
   the raw key on every request — both work).
4. Send every subsequent request with `X-API-Key: qmgr_xxxxxxxxxxxxxxxx`.

### Scopes → what they actually unlock

Each scope maps to one or more of Q-Mgr's real permission codes. A request without the right scope
gets a normal `403 Forbidden`, not a silent no-op.

| Scope | Grants |
|---|---|
| `queue:read` | View queue status (`GET /api/v1/branches/{id}/queue/status`) |
| `queue:write` | Call-next/complete/transfer operations on counters |
| `token:create` | Create new tokens (`POST /api/v1/branches/{id}/tokens`) — the main "push a customer/patient into the queue" endpoint |
| `token:manage` | Cancel tokens, plus the queue:write operations above |
| `counter:read` | View counter list/status |
| `service:read` | View service types |
| `stats:read` | View reports/analytics |

There is currently no scope for Visitor Management or Marketing/Broadcasts — those are staff-only
(JWT) surfaces for now; add scopes to the mapping in
`src/Q-Mgr.API/Authorization/PermissionAuthorizationHandler.cs` if a partner needs programmatic
access to either.

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
curl https://your-qmgr-instance/api/v1/branches/{branchId}/tokens/by-reference?externalSystem=acme_his&externalReference=HIS-APPT-4821 \
  -H "X-API-Key: qmgr_xxxxxxxxxxxxxxxx"

# 3. Read live queue status for the whole branch
curl https://your-qmgr-instance/api/v1/branches/{branchId}/queue/status \
  -H "X-API-Key: qmgr_xxxxxxxxxxxxxxxx"
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

## Known limitations

- **`ApiClient.RateLimitPerMinute` is not currently enforced.** The field exists and is shown in
  the admin UI, but nothing in the request pipeline reads it — every API key is subject only to the
  global IP-based rate limit (`IpRateLimiting` in `appsettings.json`, admin-editable, live-reloads
  without a restart), not a per-client one. Worth fixing before onboarding a real partner at volume.
- **No inbound webhook receiver.** Q-Mgr can push outbound webhooks on events (see
  `WebhookOutgoing`/`WebhookJobsRegistration`), but there's no endpoint for a partner system to push
  events *into* Q-Mgr other than the REST calls above (i.e., no "notify us when an appointment is
  cancelled" callback contract) — a partner integration is currently pull/push via the token API
  only, not full duplex.
- Visitor Management and Marketing/Broadcasts have no API-key scopes wired up yet (see above) —
  JWT/staff-only for now.
