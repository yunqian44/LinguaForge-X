using LinguaForge_X.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaForge_X.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<GetGreetingUseCase>();
        return services;
    }
}
