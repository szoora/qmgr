using Microsoft.EntityFrameworkCore;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Entities.Billing;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>See <see cref="IBillingAccountProvider"/>.</summary>
public class BillingAccountProvider : IBillingAccountProvider
{
    private readonly QMgrDbContext _dbContext;
    private readonly ILogger<BillingAccountProvider> _logger;

    public BillingAccountProvider(QMgrDbContext dbContext, ILogger<BillingAccountProvider> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Subscription> GetOrOpenAsync(Guid organizationId, DateTime asOf)
    {
        var account = await _dbContext.Subscriptions
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId &&
                                      s.Status != SubscriptionStatus.Cancelled);

        if (account != null) return account;

        // Mobile Money is the default because it is how this market pays; a card customer's
        // account is switched over when Stripe returns a payment method.
        account = new Subscription
        {
            OrganizationId = organizationId,
            Status = SubscriptionStatus.Active,
            BillingCycle = BillingCycle.Monthly,
            StartDate = asOf,
            CurrentPeriodStart = asOf,
            CurrentPeriodEnd = asOf,
            PreferredPaymentMethod = PaymentMethod.MtnMobileMoney,
            CreatedAt = asOf
        };

        _dbContext.Subscriptions.Add(account);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Opened a billing account for organization {OrganizationId}", organizationId);
        return account;
    }
}
