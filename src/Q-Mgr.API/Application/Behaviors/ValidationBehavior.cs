using FluentValidation;
using Mediator;

namespace QMgr.Application.Behaviors;

/// <summary>
/// Runs any registered FluentValidation validators for a request before its
/// handler executes. Without this, validators registered via
/// AddValidatorsFromAssembly() (see Application/DependencyInjection.cs) are
/// never actually invoked by the Mediator pipeline — they just sit in DI
/// unused. Confirmed via the dead-code pass this session that
/// CreateTokenCommandValidator was exactly in that state.
/// </summary>
public class ValidationBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    private readonly IEnumerable<IValidator<TMessage>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TMessage>> validators)
    {
        _validators = validators;
    }

    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (_validators.Any())
        {
            var context = new ValidationContext<TMessage>(message);
            var failures = (await Task.WhenAll(_validators.Select(v => v.ValidateAsync(context, cancellationToken))))
                .SelectMany(r => r.Errors)
                .Where(f => f != null)
                .ToList();

            if (failures.Count > 0)
            {
                throw new ValidationException(failures);
            }
        }

        return await next(message, cancellationToken);
    }
}
