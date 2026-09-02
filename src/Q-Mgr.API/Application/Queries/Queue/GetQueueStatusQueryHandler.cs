using System.Text.Json;
using Mediator;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;
using QMgr.Domain.Interfaces;

namespace QMgr.Application.Queries.Queue;

public class GetQueueStatusQueryHandler : IRequestHandler<GetQueueStatusQuery, QueueStatusDto>
{
    private readonly IUnitOfWork _unitOfWork;

    public GetQueueStatusQueryHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<QueueStatusDto> Handle(GetQueueStatusQuery request, CancellationToken cancellationToken)
    {
        var branch = await _unitOfWork.Branches.GetByIdAsync(request.BranchId, cancellationToken)
            ?? throw new InvalidOperationException("Branch not found.");

        var today = DateTime.UtcNow.Date;

        // Get all tokens for today
        var tokens = await _unitOfWork.Tokens.GetTokensByStatusAsync(
            request.BranchId, TokenStatus.Waiting, today, cancellationToken);

        var completedTokens = await _unitOfWork.Tokens.GetTokensByStatusAsync(
            request.BranchId, TokenStatus.Completed, today, cancellationToken);

        var servingTokens = await _unitOfWork.Tokens.GetTokensByStatusAsync(
            request.BranchId, TokenStatus.Serving, today, cancellationToken);

        // Get service types
        var serviceTypes = await _unitOfWork.ServiceTypes.FindAsync(
            st => st.BranchId == request.BranchId && st.IsActive, cancellationToken);

        // Get counters
        var counters = await _unitOfWork.Counters.FindAsync(
            c => c.BranchId == request.BranchId && c.IsActive, cancellationToken);

        // Calculate averages. BUG FIX: guarding on completedTokens.Any() doesn't guarantee the
        // *filtered* sequence below is non-empty — a branch can have completed tokens today
        // where none of them happen to have ActualWaitMinutes/ServiceDurationMinutes set (e.g.
        // a token called and completed without ever transitioning through Serving). Average()
        // on an empty sequence throws "Sequence contains no elements", which was surfacing live
        // as a raw 400 on the dashboard's queue-status widget. Guard on the filtered list itself.
        var tokensWithWait = completedTokens.Where(t => t.ActualWaitMinutes.HasValue).ToList();
        var avgWait = tokensWithWait.Any() ? tokensWithWait.Average(t => t.ActualWaitMinutes!.Value) : 0;

        var tokensWithServiceDuration = completedTokens.Where(t => t.ServiceDurationMinutes.HasValue).ToList();
        var avgService = tokensWithServiceDuration.Any() ? tokensWithServiceDuration.Average(t => t.ServiceDurationMinutes!.Value) : 0;

        return new QueueStatusDto
        {
            BranchId = branch.Id,
            BranchName = branch.Name,
            CurrentTime = DateTime.UtcNow,
            Summary = new QueueSummaryDto
            {
                TotalWaiting = tokens.Count,
                TotalServing = servingTokens.Count,
                TotalCompletedToday = completedTokens.Count,
                AverageWaitMinutes = avgWait,
                AverageServiceMinutes = avgService
            },
            ServiceTypes = serviceTypes.Select(st => new ServiceTypeQueueDto
            {
                Id = st.Id,
                Code = st.Code,
                Name = st.Name,
                WaitingCount = tokens.Count(t => t.ServiceTypeId == st.Id),
                EstimatedWaitMinutes = st.AverageServiceTimeMinutes * tokens.Count(t => t.ServiceTypeId == st.Id),
                CountersActive = counters.Count(c => c.Status == CounterStatus.Active &&
                    c.CounterServiceTypes.Any(cst => cst.ServiceTypeId == st.Id))
            }).ToList(),
            Counters = counters.Select(c => new CounterStatusDto
            {
                Id = c.Id,
                CounterNumber = c.CounterNumber,
                DisplayName = c.DisplayName,
                Status = c.Status.ToString(),
                ServiceTypeCodes = c.CounterServiceTypes.Select(cst => cst.ServiceType?.Code ?? "").ToList(),
                CurrentTokenDisplay = c.CurrentToken?.DisplayNumber,
                ServingCustomerName = c.CurrentToken?.CustomerName,
                TokensServedToday = completedTokens.Count(t => t.CounterId == c.Id)
            }).ToList()
        };
    }
}

public class GetTokenQueryHandler : IRequestHandler<GetTokenQuery, TokenDto?>
{
    private readonly IUnitOfWork _unitOfWork;

    public GetTokenQueryHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<TokenDto?> Handle(GetTokenQuery request, CancellationToken cancellationToken)
    {
        var token = await _unitOfWork.Tokens.GetByIdAsync(request.TokenId, cancellationToken);
        // SECURITY: token lookup is by GUID alone — without this check, any caller who owns
        // any branch in their org can read another tenant's token (PII) by guessing/enumerating
        // a tokenId, since VerifyBranchOwnership only proves the caller owns *a* branch, not
        // that this specific token belongs to it.
        if (token == null || token.BranchId != request.BranchId) return null;

        var position = token.Status == TokenStatus.Waiting
            ? await _unitOfWork.Tokens.GetQueuePositionAsync(token.Id, cancellationToken)
            : (int?)null;

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
            CounterId = token.CounterId,
            Customer = new CustomerDto
            {
                Id = token.CustomerId,
                Name = token.CustomerName,
                Phone = token.CustomerPhone,
                Email = token.CustomerEmail
            },
            ExternalReference = token.ExternalReference,
            ExternalSystem = token.ExternalSystem,
            Metadata = token.Metadata != null ? JsonSerializer.Deserialize<Dictionary<string, object>>(token.Metadata) : null,
            PositionInQueue = position,
            EstimatedWaitMinutes = token.EstimatedWaitMinutes,
            ActualWaitMinutes = token.ActualWaitMinutes,
            ServiceDurationMinutes = token.ServiceDurationMinutes,
            CreatedAt = token.CreatedAt,
            CalledAt = token.CalledAt,
            ServiceStartedAt = token.ServiceStartedAt,
            ServiceCompletedAt = token.ServiceCompletedAt
        };
    }
}

public class GetTokenByExternalReferenceQueryHandler : IRequestHandler<GetTokenByExternalReferenceQuery, TokenDto?>
{
    private readonly IUnitOfWork _unitOfWork;

    public GetTokenByExternalReferenceQueryHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<TokenDto?> Handle(GetTokenByExternalReferenceQuery request, CancellationToken cancellationToken)
    {
        var token = await _unitOfWork.Tokens.GetByExternalReferenceAsync(
            request.BranchId, request.ExternalSystem, request.ExternalReference, cancellationToken);
        if (token == null) return null;

        var position = token.Status == TokenStatus.Waiting
            ? await _unitOfWork.Tokens.GetQueuePositionAsync(token.Id, cancellationToken)
            : (int?)null;

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
            CounterId = token.CounterId,
            Customer = new CustomerDto
            {
                Id = token.CustomerId,
                Name = token.CustomerName,
                Phone = token.CustomerPhone,
                Email = token.CustomerEmail
            },
            ExternalReference = token.ExternalReference,
            ExternalSystem = token.ExternalSystem,
            Metadata = token.Metadata != null ? JsonSerializer.Deserialize<Dictionary<string, object>>(token.Metadata) : null,
            PositionInQueue = position,
            EstimatedWaitMinutes = token.EstimatedWaitMinutes,
            ActualWaitMinutes = token.ActualWaitMinutes,
            ServiceDurationMinutes = token.ServiceDurationMinutes,
            CreatedAt = token.CreatedAt,
            CalledAt = token.CalledAt,
            ServiceStartedAt = token.ServiceStartedAt,
            ServiceCompletedAt = token.ServiceCompletedAt
        };
    }
}

public class GetTokensByCustomerQueryHandler : IRequestHandler<GetTokensByCustomerQuery, List<TokenDto>>
{
    private readonly IUnitOfWork _unitOfWork;

    public GetTokensByCustomerQueryHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<List<TokenDto>> Handle(GetTokensByCustomerQuery request, CancellationToken cancellationToken)
    {
        var tokens = await _unitOfWork.Tokens.GetTokensByCustomerIdAsync(
            request.BranchId, request.CustomerId, cancellationToken);

        if (request.ActiveOnly)
        {
            tokens = tokens.Where(t => t.Status is TokenStatus.Waiting or TokenStatus.Called or TokenStatus.Serving).ToList();
        }

        var result = new List<TokenDto>();
        foreach (var token in tokens)
        {
            var position = token.Status == TokenStatus.Waiting
                ? await _unitOfWork.Tokens.GetQueuePositionAsync(token.Id, cancellationToken)
                : (int?)null;

            result.Add(new TokenDto
            {
                Id = token.Id,
                TokenNumber = token.TokenNumber,
                DisplayNumber = token.DisplayNumber,
                Status = token.Status,
                Priority = token.Priority,
                Source = token.Source,
                BranchId = token.BranchId,
                ServiceTypeId = token.ServiceTypeId,
                CounterId = token.CounterId,
                Customer = new CustomerDto
                {
                    Id = token.CustomerId,
                    Name = token.CustomerName,
                    Phone = token.CustomerPhone,
                    Email = token.CustomerEmail
                },
                ExternalReference = token.ExternalReference,
                ExternalSystem = token.ExternalSystem,
                Metadata = token.Metadata != null ? JsonSerializer.Deserialize<Dictionary<string, object>>(token.Metadata) : null,
                PositionInQueue = position,
                EstimatedWaitMinutes = token.EstimatedWaitMinutes,
                ActualWaitMinutes = token.ActualWaitMinutes,
                ServiceDurationMinutes = token.ServiceDurationMinutes,
                CreatedAt = token.CreatedAt,
                CalledAt = token.CalledAt,
                ServiceStartedAt = token.ServiceStartedAt,
                ServiceCompletedAt = token.ServiceCompletedAt
            });
        }

        return result;
    }
}

public class GetWaitingTokensQueryHandler : IRequestHandler<GetWaitingTokensQuery, List<TokenDto>>
{
    private readonly IUnitOfWork _unitOfWork;

    public GetWaitingTokensQueryHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<List<TokenDto>> Handle(GetWaitingTokensQuery request, CancellationToken cancellationToken)
    {
        var today = DateTime.UtcNow.Date;
        var tokens = await _unitOfWork.Tokens.GetTokensByStatusAsync(
            request.BranchId, TokenStatus.Waiting, today, cancellationToken);

        // Filter by service type if specified
        if (request.ServiceTypeId.HasValue)
        {
            tokens = tokens.Where(t => t.ServiceTypeId == request.ServiceTypeId.Value).ToList();
        }

        // Order by priority (VIP > Priority > Normal) then by creation time
        tokens = tokens
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        // Apply limit if specified
        if (request.Limit.HasValue && request.Limit.Value > 0)
        {
            tokens = tokens.Take(request.Limit.Value).ToList();
        }

        var result = new List<TokenDto>();
        var position = 1;

        foreach (var token in tokens)
        {
            var serviceType = await _unitOfWork.ServiceTypes.GetByIdAsync(token.ServiceTypeId, cancellationToken);

            result.Add(new TokenDto
            {
                Id = token.Id,
                TokenNumber = token.TokenNumber,
                DisplayNumber = token.DisplayNumber,
                Status = token.Status,
                Priority = token.Priority,
                Source = token.Source,
                BranchId = token.BranchId,
                ServiceTypeId = token.ServiceTypeId,
                CounterId = token.CounterId,
                Customer = new CustomerDto
                {
                    Id = token.CustomerId,
                    Name = token.CustomerName,
                    Phone = token.CustomerPhone,
                    Email = token.CustomerEmail
                },
                ServiceType = serviceType != null ? new ServiceTypeDto
                {
                    Id = serviceType.Id,
                    Name = serviceType.Name,
                    Code = serviceType.Code,
                    Description = serviceType.Description,
                    Prefix = serviceType.Prefix,
                    AverageServiceTimeMinutes = serviceType.AverageServiceTimeMinutes,
                    Color = serviceType.Color
                } : null,
                ExternalReference = token.ExternalReference,
                ExternalSystem = token.ExternalSystem,
                Metadata = token.Metadata != null ? JsonSerializer.Deserialize<Dictionary<string, object>>(token.Metadata) : null,
                PositionInQueue = position++,
                EstimatedWaitMinutes = token.EstimatedWaitMinutes,
                CreatedAt = token.CreatedAt
            });
        }

        return result;
    }
}
