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

## Consumir la API

`docs/api-contract.md` es el contrato para quien construye un cliente: la secuencia de autenticación, por qué hay que refrescar el token de forma proactiva y no al recibir un 401, los techos de tamaño y de rate limit por endpoint, las dos únicas respuestas de error que no traen JSON, y cómo levantar la API local. Está en inglés, igual que el resto del código y los comentarios.

El documento OpenAPI (`/openapi/v1.json`, solo en Development) sigue siendo la fuente de las *formas*: rutas, campos y códigos de estado se generan desde el código y no pueden desincronizarse. `api-contract.md` cubre lo que un esquema no puede expresar.

## Arquitectura

```
src/BuildCv.Domain/         → Entidades puras, 0 dependencias externas
src/BuildCv.Application/    → Features + puertos de IO
src/BuildCv.Infrastructure/ → Adaptadores concretos
src/BuildCv.Api/            → Minimal APIs + composición
```

## Regla de comprensión

No se mergea un PR que no puedas explicar línea por línea en voz alta.
