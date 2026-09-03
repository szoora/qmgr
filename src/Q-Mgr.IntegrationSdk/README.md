# Q-Mgr Integration SDK

A small client library for systems that need to put people into a Q-Mgr queue and read back where
they are: hospital and clinic management systems, banking cores, pharmacy systems, or anything
else that already knows a customer is arriving.

It talks to Q-Mgr's public REST API over HTTPS and has no dependency on Q-Mgr's own server code, so
it is meant to be referenced from **your** application, not from Q-Mgr.

## Getting credentials

An administrator creates an API client for you in Q-Mgr under **Integrations → API Clients** and
gives you two values:

| Value | Where it is used |
|---|---|
| Client ID | Identifies your integration |
| Client Secret | Proves it is you. Shown once at creation, and again only if rotated |

Both are required on every request. A client id on its own is not a credential.

## Usage

```csharp
var options = new QueueIntegrationOptions
{
    ApiBaseUrl = "https://qmgr.example.com",
    ApiKey     = "qmgr_...",      // client id
    ApiSecret  = "qmgr_sk_...",   // client secret
    BranchId   = branchId,
    SystemIdentifier = "acme_his"
};

var client = new QueueIntegrationClient(httpClient, logger, options);

var result = await client.CreateTokenAsync(new CreateTokenRequest
{
    ServiceTypeCode   = "GEN",
    Customer          = new { Name = "Jane Doe", Phone = "+256700000000" },
    ExternalReference = "HIS-4821"   // your own id, so you never have to store ours
});
```

`ExternalReference` is the important field. Put the identifier the person already has in your
system there and you can look them up later without persisting anything of Q-Mgr's:

```csharp
var status = await client.GetTokenByExternalReferenceAsync("acme_his", "HIS-4821");
```

## Scopes

Ask only for what you need. A key that creates tickets should not also be able to cancel them.
The full list is in the API integration guide; the common ones are `token:create`, `token:read`,
`token:manage`, `queue:read`, `counter:read`, `service:read` and `stats:read`.

## Webhooks

Q-Mgr can call you when a ticket is created, called, served, completed, cancelled or marked as a
no-show. Deliveries carry `X-QMgr-Event` and `X-QMgr-Signature`.

**Verify the signature before trusting the payload**, using the raw request bytes:

```csharp
// Read the body before model binding: the signature covers the exact bytes sent,
// and deserializing then re-serializing changes them.
using var reader = new StreamReader(Request.Body);
var rawBody = await reader.ReadToEndAsync();

if (!QMgrWebhookVerifier.IsValid(rawBody, Request.Headers["X-QMgr-Signature"], webhookSecret))
{
    return Unauthorized();
}
```

`QMgrWebhookVerifier` compares in constant time and tolerates the header with or without its
`sha256=` prefix. Use `ComputeSignature` to sign events you push **into** Q-Mgr's inbound endpoint.

## Testing your integration

There is no separate sandbox host. Ask your administrator for a branch used only for testing and
an API client restricted to it with `AllowedBranches`; tickets you create there stay out of the
branches your colleagues are working in. Rotate the secret when you go live.

## Rate limits

Each API client has a requests-per-minute limit set by the administrator. Exceeding it returns
`429` with a `Retry-After` header giving the seconds until the window resets. Honour it rather
than retrying immediately.
