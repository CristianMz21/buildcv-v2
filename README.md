# BuildCv v2

Reconstrucción desde cero del analizador de hojas de vida — puntaje determinista de coincidencia y legibilidad para sistemas automáticos.

**Producto público gratuito** para buscadores de empleo hispanohablantes.

## Los dos repositorios

BuildCv v2 son dos repos separados, cada uno con su propio ciclo de vida:

| Repo | Qué es |
|---|---|
| **`buildcv-v2`** (este) | La API. .NET 10, Clean Architecture, sin ninguna dependencia del cliente. |
| **[`buildcv-v2-web`](https://github.com/CristianMz21/buildcv-v2-web)** | El cliente. Next.js, habla con esta API **server-side desde su BFF**, nunca desde el browser. |

Están separados a propósito, y el costo está aceptado: **un cambio de contrato y el arreglo de su cliente ya no pueden ser un solo commit.** A cambio, cada uno despliega, versiona y falla por su cuenta.

Lo que mantiene a los dos alineados es **[`docs/api-contract.md`](docs/api-contract.md)**: el contrato escrito para quien consume esta API, con la secuencia de autenticación, los límites de tamaño y frecuencia, y las trampas que OpenAPI no puede expresar. Sus números están afirmados contra las constantes del código por `ApiContractDocumentTests`, así que no pueden quedar obsoletos en silencio.

Como el cliente usa un BFF, **CORS, `SameSite` y `Cross-Origin-Resource-Policy` no son problemas de este repo y no deben relajarse** — el tráfico servidor-a-servidor no está sujeto a ninguno de los tres.

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

## Desplegar

`docs/deployment.md` es el runbook para quien lo pone en producción, escrito para operar y no para modificar el código. Empieza por lo único que puede causar pérdida irrecuperable: **el anillo de claves de cifrado no está en la base de datos**, así que un backup de SQL Server contiene el texto cifrado y ninguna clave — perderlo deja cada CV ilegible para siempre. Cubre además la configuración de `Network:ForwardedHeaders` sin la cual el rate limiting colapsa en una denegación de servicio autoinfligida, por qué SQL Server en el compose no está licenciado para producción, las dos migraciones que destruyen datos, y la checklist previa a anunciarlo.

## Arquitectura

```
src/BuildCv.Domain/         → Entidades puras, 0 dependencias externas
src/BuildCv.Application/    → Features + puertos de IO
src/BuildCv.Infrastructure/ → Adaptadores concretos
src/BuildCv.Api/            → Minimal APIs + composición
```

## Regla de comprensión

No se mergea un PR que no puedas explicar línea por línea en voz alta.

## Licencia

Elastic License 2.0 — el mismo texto que el cliente `buildcv-web`, de modo que los dos repos del producto comparten una sola licencia. Ver [`LICENSE`](LICENSE).
