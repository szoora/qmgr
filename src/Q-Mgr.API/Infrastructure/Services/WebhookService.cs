using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Integration;
using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Interfaces;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Data.Repositories;

namespace QMgr.Infrastructure.Services;

public class WebhookService : IWebhookService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IModuleAccessService _moduleAccessService;
    private readonly ILogger<WebhookService> _logger;

    public WebhookService(
        IUnitOfWork unitOfWork,
        IHttpClientFactory httpClientFactory,
        IModuleAccessService moduleAccessService,
        ILogger<WebhookService> logger)
    {
        _unitOfWork = unitOfWork;
        _httpClientFactory = httpClientFactory;
        _moduleAccessService = moduleAccessService;
        _logger = logger;
    }

    public async Task TriggerTokenCreatedAsync(Token token, CancellationToken cancellationToken = default)
    {
        await TriggerWebhookAsync("token.created", token, cancellationToken);
    }

    public async Task TriggerTokenCalledAsync(Token token, CancellationToken cancellationToken = default)
    {
        await TriggerWebhookAsync("token.called", token, cancellationToken);
    }

    public async Task TriggerTokenServingAsync(Token token, CancellationToken cancellationToken = default)
    {
        await TriggerWebhookAsync("token.serving", token, cancellationToken);
    }

    public async Task TriggerTokenCompletedAsync(Token token, CancellationToken cancellationToken = default)
    {
        await TriggerWebhookAsync("token.completed", token, cancellationToken);
    }

    public async Task TriggerTokenCancelledAsync(Token token, CancellationToken cancellationToken = default)
    {
        await TriggerWebhookAsync("token.cancelled", token, cancellationToken);
    }

    public async Task TriggerTokenNoShowAsync(Token token, CancellationToken cancellationToken = default)
    {
        await TriggerWebhookAsync("token.no_show", token, cancellationToken);
    }

    private async Task TriggerWebhookAsync(string eventType, Token token, CancellationToken cancellationToken)
    {
        // Find API clients subscribed to this event
        // Note: We fetch active clients with webhooks first, then filter in memory
        // because EF Core can't translate string[].Contains() to SQL
        var allActiveClients = await _unitOfWork.ApiClients.FindAsync(
            ac => ac.IsActive && ac.WebhookUrl != null,
            cancellationToken);

        var apiClients = allActiveClients
            .Where(ac => ac.WebhookEvents != null && ac.WebhookEvents.Contains(eventType))
            .ToList();

        if (apiClients.Count == 0)
            return;

        // MODULE GATING: outbound webhooks are part of the Integrations API module. A client
        // whose organization no longer has that module active (trial lapsed, module revoked)
        // keeps its configuration but gets no deliveries until the module is active again.
        // Checked once per organization per event, not once per client.
        var moduleActiveByOrg = new Dictionary<Guid, bool>();

        foreach (var client in apiClients)
        {
            if (!moduleActiveByOrg.TryGetValue(client.OrganizationId, out var moduleActive))
            {
                moduleActive = await _moduleAccessService.IsModuleActiveAsync(client.OrganizationId, ModuleCodes.IntegrationsApi);
                moduleActiveByOrg[client.OrganizationId] = moduleActive;
            }

            if (!moduleActive)
            {
                _logger.LogDebug(
                    "Skipping webhook {EventType} for API client {ApiClientId}: organization {OrganizationId} does not have the {Module} module active",
                    eventType, client.Id, client.OrganizationId, ModuleCodes.IntegrationsApi);
                continue;
            }

            // Check if client has access to this branch
            if (client.AllowedBranches != null && client.AllowedBranches.Length > 0)
            {
                if (!client.AllowedBranches.Contains(token.BranchId))
                    continue;
            }

            var payload = CreateWebhookPayload(eventType, token);

            // Queue webhook for delivery
            var webhook = new WebhookOutgoing
            {
                ApiClientId = client.Id,
                EventType = eventType,
                Payload = JsonSerializer.Serialize(payload),
                Status = "pending"
            };

            await _context.WebhooksOutgoing.AddAsync(webhook, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private QMgrDbContext _context => ((UnitOfWork)_unitOfWork).GetContext();

    private object CreateWebhookPayload(string eventType, Token token)
    {
        return new
        {
            @event = eventType,
            timestamp = DateTime.UtcNow,
            data = new
            {
                token = new
                {
                    id = token.Id,
                    display_number = token.DisplayNumber,
                    status = token.Status.ToString().ToLower(),
                    counter = token.Counter != null ? new
                    {
                        id = token.CounterId,
                        number = token.Counter.CounterNumber
                    } : null,
                    customer = new
                    {
                        id = token.CustomerId,
                        name = token.CustomerName
                    },
                    external_reference = token.ExternalReference,
                    wait_time_minutes = token.ActualWaitMinutes
                },
                branch = new
                {
                    id = token.BranchId
                }
            }
        };
    }

    public async Task ProcessPendingWebhooksAsync(CancellationToken cancellationToken = default)
    {
        var pendingWebhooks = await _context.WebhooksOutgoing
            .Where(w => w.Status == "pending" || (w.Status == "retrying" && w.Attempts < 5))
            .OrderBy(w => w.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var webhook in pendingWebhooks)
        {
            await DeliverWebhookAsync(webhook, cancellationToken);
        }
    }

    private async Task DeliverWebhookAsync(WebhookOutgoing webhook, CancellationToken cancellationToken)
    {
        var client = await _unitOfWork.ApiClients.GetByIdAsync(webhook.ApiClientId, cancellationToken);
        if (client?.WebhookUrl == null)
        {
            webhook.Status = "failed";
            webhook.LastError = "API client or webhook URL not found";
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient("Webhook");

            var request = new HttpRequestMessage(HttpMethod.Post, client.WebhookUrl);
            request.Content = new StringContent(webhook.Payload ?? "{}", Encoding.UTF8, "application/json");

            // Add signature header
            if (!string.IsNullOrEmpty(client.WebhookSecret))
            {
                var signature = ComputeSignature(webhook.Payload ?? "{}", client.WebhookSecret);
                request.Headers.Add("X-QMgr-Signature", $"sha256={signature}");
            }
            request.Headers.Add("X-QMgr-Event", webhook.EventType);

            var response = await httpClient.SendAsync(request, cancellationToken);

            webhook.Attempts++;
            webhook.LastAttemptAt = DateTime.UtcNow;

            if (response.IsSuccessStatusCode)
            {
                webhook.Status = "sent";
            }
            else
            {
                webhook.Status = webhook.Attempts >= 5 ? "failed" : "retrying";
                webhook.LastError = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            }
        }
        catch (Exception ex)
        {
            webhook.Attempts++;
            webhook.LastAttemptAt = DateTime.UtcNow;
            webhook.Status = webhook.Attempts >= 5 ? "failed" : "retrying";
            webhook.LastError = ex.Message;

            _logger.LogError(ex, "Failed to deliver webhook {WebhookId} to {Url}", webhook.Id, client.WebhookUrl);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static string ComputeSignature(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLower();
    }
}

// Extension for UnitOfWork to access DbContext
public static class UnitOfWorkExtensions
{
    public static QMgrDbContext GetContext(this UnitOfWork unitOfWork)
    {
        var field = typeof(UnitOfWork).GetField("_context", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (QMgrDbContext)field!.GetValue(unitOfWork)!;
    }
}
