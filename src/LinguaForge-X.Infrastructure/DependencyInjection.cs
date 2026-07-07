using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaForge_X.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IGreetingRepository, LocalGreetingRepository>();
        return services;
    }
}
