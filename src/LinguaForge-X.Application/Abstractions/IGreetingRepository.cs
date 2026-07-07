using LinguaForge_X.Domain.Entities;

namespace LinguaForge_X.Application.Abstractions;

public interface IGreetingRepository
{
    Greeting GetGreeting();
}
