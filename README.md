# LinguaForge-X

WPF desktop app built with **.NET 10** and a minimal **Clean Architecture** layout.

## Project structure

- `src/LinguaForge-X.Domain`: domain entities
- `src/LinguaForge-X.Application`: use cases and abstractions
- `src/LinguaForge-X.Infrastructure`: implementation of application abstractions
- `src/LinguaForge-X.Presentation.Wpf`: WPF UI and composition root

## Run

```powershell
dotnet restore
dotnet run --project src\LinguaForge-X.Presentation.Wpf\LinguaForge-X.Presentation.Wpf.csproj
```
