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

Los tests de integración (`[Trait("Category", "Integration")]`) requieren un daemon de Docker corriendo local: levantan y migran su propio contenedor SQL Server 2022 descartable vía `Testcontainers.MsSql` — no usan la instancia de `docker-compose.yml`, así que no hace falta `docker compose up` para correrlos. `dotnet test --filter "Category!=Integration"` corre solo los unitarios; `--filter "Category=Integration"` corre solo los de integración. `docker-compose.yml` es para desarrollo manual y `dotnet ef database update`, un propósito distinto.

## Arquitectura

```
src/BuildCv.Domain/         → Entidades puras, 0 dependencias externas
src/BuildCv.Application/    → Features + puertos de IO
src/BuildCv.Infrastructure/ → Adaptadores concretos
src/BuildCv.Api/            → Minimal APIs + composición
```

## Regla de comprensión

No se mergea un PR que no puedas explicar línea por línea en voz alta.
