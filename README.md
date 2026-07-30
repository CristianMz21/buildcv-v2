# BuildCv v2

Reconstrucción desde cero del analizador de hojas de vida — puntaje determinista de coincidencia y legibilidad para sistemas automáticos.

**Producto público gratuito** para buscadores de empleo hispanohablantes.

## Stack

- **Backend**: .NET 10, ASP.NET Core, Clean Architecture
- **Tests**: xUnit + FluentAssertions
- **CI**: GitHub Actions

## Getting started

```bash
dotnet build BuildCv.slnx -c Release
dotnet test
dotnet run --project src/BuildCv.Api
```

## Arquitectura

```
src/BuildCv.Domain/         → Entidades puras, 0 dependencias externas
src/BuildCv.Application/    → Features + puertos de IO
src/BuildCv.Infrastructure/ → Adaptadores concretos
src/BuildCv.Api/            → Minimal APIs + composición
```

## Regla de comprensión

No se mergea un PR que no puedas explicar línea por línea en voz alta.
