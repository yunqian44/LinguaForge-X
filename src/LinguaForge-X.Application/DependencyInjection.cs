using LinguaForge_X.Application.UseCases;
using LinguaForge_X.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaForge_X.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IConversionService, ConversionService>();
        return services;
    }
}
