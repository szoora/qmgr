using FluentValidation;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using QMgr.Application.Behaviors;
using System.Reflection;

namespace QMgr.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        // Mediator (source-generated) - automatically discovers handlers
        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
        });

        // Runs any registered FluentValidation validators before a request's
        // handler executes — without this, validators are registered but
        // never actually invoked (see Behaviors/ValidationBehavior.cs).
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        services.AddValidatorsFromAssembly(assembly);

        return services;
    }
}
