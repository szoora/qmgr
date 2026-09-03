using System.Text.Json;
using Mediator;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Interfaces;

namespace QMgr.Application.Commands.Queue;

public class CreateTokenCommandHandler : IRequestHandler<CreateTokenCommand, TokenDto>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IQueueService _queueService;
    private readonly IWebhookService _webhookService;
    private readonly IUsageTrackingService _usageTrackingService;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IQueueCustomerNotifier _customerNotifier;

    public CreateTokenCommandHandler(
        IUnitOfWork unitOfWork,
        IQueueService queueService,
        IWebhookService webhookService,
        IUsageTrackingService usageTrackingService,
        ITenantContextAccessor tenantContextAccessor,
        IQueueCustomerNotifier customerNotifier)
    {
        _unitOfWork = unitOfWork;
        _queueService = queueService;
        _webhookService = webhookService;
        _usageTrackingService = usageTrackingService;
        _tenantContextAccessor = tenantContextAccessor;
        _customerNotifier = customerNotifier;
    }

    public async ValueTask<TokenDto> Handle(CreateTokenCommand request, CancellationToken cancellationToken)
    {
        // Get service type by code
        var serviceType = await _unitOfWork.ServiceTypes
            .FirstOrDefaultAsync(st => st.BranchId == request.BranchId && st.Code == request.ServiceTypeCode, cancellationToken)
            ?? throw new InvalidOperationException($"Service type '{request.ServiceTypeCode}' not found.");

        // Calculate estimated wait time
        var estimatedWait = await _queueService.CalculateEstimatedWaitAsync(request.BranchId, serviceType.Id, cancellationToken);

        // CONCURRENCY: number generation + insert must run in the same transaction as the
        // advisory lock taken inside GetNextTokenNumberAsync, or the lock has nothing to
        // serialize against — see that method's comment for the race this closes.
        Token token = null!;
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var tokenNumber = await _unitOfWork.Tokens.GetNextTokenNumberAsync(request.BranchId, serviceType.Id, ct);
            var displayNumber = $"{serviceType.Prefix}{tokenNumber:D3}";

            token = new Token
            {
                BranchId = request.BranchId,
                ServiceTypeId = serviceType.Id,
                TokenNumber = tokenNumber.ToString(),
                DisplayNumber = displayNumber,
                CustomerId = request.Customer?.Id,
                CustomerName = request.Customer?.Name,
                CustomerPhone = request.Customer?.Phone,
                CustomerEmail = request.Customer?.Email,
                Source = request.Source,
                Priority = request.Priority,
                ExternalReference = request.ExternalReference,
                ExternalSystem = request.ExternalSystem,
                EstimatedWaitMinutes = estimatedWait,
                Metadata = request.Metadata != null ? JsonSerializer.Serialize(request.Metadata) : null
            };

            await _unitOfWork.Tokens.AddAsync(token, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }, cancellationToken);

        // Get queue position
        var position = await _unitOfWork.Tokens.GetQueuePositionAsync(token.Id, cancellationToken);

        // Trigger webhook
        await _webhookService.TriggerTokenCreatedAsync(token, cancellationToken);

        // Tell the customer their ticket exists. Deliberately AFTER the transaction above has
        // committed — the ticket is real whether or not the SMS gateway is reachable. The
        // notifier itself never throws and never blocks (see IQueueCustomerNotifier), and sends
        // nothing at all unless this customer actually supplied a phone number or email.
        await _customerNotifier.NotifyTicketIssuedAsync(token.Id, position);

        // Track token creation for usage metering
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext?.IsResolved == true)
        {
            await _usageTrackingService.IncrementTokensCreatedAsync(tenantContext.OrganizationId);
        }

        return new TokenDto
        {
            Id = token.Id,
            TokenNumber = token.TokenNumber,
            DisplayNumber = token.DisplayNumber,
            Status = token.Status,
            Priority = token.Priority,
            Source = token.Source,
            BranchId = token.BranchId,
            ServiceTypeId = token.ServiceTypeId,
            Customer = request.Customer,
            ServiceType = new ServiceTypeDto
            {
                Id = serviceType.Id,
                Name = serviceType.Name,
                Code = serviceType.Code,
                Prefix = serviceType.Prefix
            },
            ExternalReference = token.ExternalReference,
            ExternalSystem = token.ExternalSystem,
            PositionInQueue = position,
            EstimatedWaitMinutes = estimatedWait,
            CreatedAt = token.CreatedAt,
            Metadata = request.Metadata
        };
    }
}
