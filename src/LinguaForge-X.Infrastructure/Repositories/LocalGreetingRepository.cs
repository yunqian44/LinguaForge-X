using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Entities;

namespace LinguaForge_X.Infrastructure.Repositories;

public sealed class LocalGreetingRepository : IGreetingRepository
{
    public Greeting GetGreeting()
    {
        return new Greeting("Hello from .NET 10 WPF with Clean Architecture.");
    }
}
