using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Entities;

namespace LinguaForge_X.Application.UseCases;

public sealed class GetGreetingUseCase
{
    private readonly IGreetingRepository _greetingRepository;

    public GetGreetingUseCase(IGreetingRepository greetingRepository)
    {
        _greetingRepository = greetingRepository;
    }

    public Greeting Execute()
    {
        return _greetingRepository.GetGreeting();
    }
}
