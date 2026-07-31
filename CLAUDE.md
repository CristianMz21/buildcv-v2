# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

BuildCv — deterministic resume/CV match-and-readability scorer for ATS systems, aimed at Spanish-speaking job seekers. .NET 10 (SDK pinned to `10.0.100` in `global.json`), ASP.NET Core Minimal APIs, Clean Architecture. The solution file is the XML-style `BuildCv.slnx` (not a classic `.sln`).

Team rule from the README: don't merge a PR you can't explain line-by-line out loud. The codebase deliberately avoids MediatR, AutoMapper, and mocking libraries in favor of explicit, hand-written code.

## Commands

```bash
dotnet build BuildCv.slnx -c Release          # build
dotnet run --project src/BuildCv.Api          # run the API
dotnet test BuildCv.slnx                      # all tests
dotnet format BuildCv.slnx                    # format (CI enforces --verify-no-changes)
```

Run a single test class or method (xUnit filter):

```bash
dotnet test tests/BuildCv.Api.Tests/BuildCv.Api.Tests.csproj --filter "FullyQualifiedName~AuthFlowTests"
dotnet test tests/BuildCv.Domain.Tests/BuildCv.Domain.Tests.csproj --filter "FullyQualifiedName~ResumeTests.AddSkill_Duplicate_Throws"
```

CI (`.github/workflows/ci.yml`): restore → build Release → `dotnet format --verify-no-changes` → test with coverage. Unformatted code fails CI, so run `dotnet format` before committing.

## Architecture

Clean Architecture; dependencies point inward. Each `src/` project has a mirror test project in `tests/`.

```
BuildCv.Domain          no dependencies (0 NuGet packages)
BuildCv.Application     → Domain          (use cases + ports)
BuildCv.Infrastructure  → Application     (adapters: Argon2id hasher, JWT TokenService, in-memory repositories)
BuildCv.Api             → Application + Infrastructure (Minimal APIs + composition root)
```

- **Domain** (`src/BuildCv.Domain`): bounded contexts as folders (`Identity/`, `Resumes/`, `Jobs/`, `Organizations/`, `Scoring/`, `Common/ValueObjects/`), one file per type. Entities are `sealed class` with private constructors and static `Create(...)` factories; collections exposed as `IReadOnlyList<T>` and mutated only through methods that call a private `Touch()` to bump `UpdatedAt`. Value objects are `sealed record` with `Create`/`TryCreate` factories. Invariant violations throw typed `DomainException` subclasses from `Domain/Exceptions/DomainException.cs`.
- **Application** (`src/BuildCv.Application`): hand-rolled CQRS — `ICommand<T>`/`IQuery<T>` + handler interfaces in `Common/Abstractions/`, no MediatR; handlers are registered directly in DI. One file per use case named `<Verb><Noun>.cs` containing both the command/query record and its handler (e.g. `Identity/Login.cs`). Ports live in `Common/Repositories/` and `Common/Services/` (`IPasswordHasher`, `ITokenService`, `IScoringEngine`).
- **Infrastructure** (`src/BuildCv.Infrastructure`): `DependencyInjection.AddInfrastructure(...)` is the single registration point for all handlers and adapters. Persistence is in-memory only so far. `JwtSettings.SigningKey` must be ≥32 chars (validated at startup).
- **Api** (`src/BuildCv.Api`): endpoints grouped per feature as `Map*Endpoints()` extensions in `Endpoints/`; request/response DTOs are sealed records in `Contracts/`, mapped to Application commands inside the endpoint lambdas — never reuse Application types as wire contracts. `Program.cs` ends with `public partial class Program;` for the test factory.

### Error handling — three-tier convention

Follow this strictly; don't mix tiers:

1. **Domain** throws typed `DomainException` subclasses for invariant violations.
2. **Application** handlers catch `DomainException`/`ArgumentException` and return `Result<T>` (`Domain/Common/ValueObjects/Result.cs`, with `Map`/`Bind`/`Match`).
3. **Api** converts `Result<T>` to HTTP via `Common/ResultExtensions.ToHttpResult()` (403 for "Forbidden.", 404 for errors ending in "not found.", else 400); anything that leaks is turned into RFC 7807 ProblemDetails by the `IExceptionHandler`s in `Common/ApiExceptionHandlers.cs`. All error responses are ProblemDetails-shaped.

### API security model

Auth is JWT in HttpOnly cookies (with `Authorization: Bearer` fallback — `OnMessageReceived` reads the `access_token` cookie). Refresh tokens are opaque, cookie-scoped to `/auth/refresh`. Cross-cutting pieces live in `src/BuildCv.Api/Security/`:

- `CsrfGuardMiddleware`: double-submit-cookie CSRF check (`X-XSRF-TOKEN` header) for unsafe methods on cookie-authenticated requests only; bearer requests are exempt by design. "Bearer request" means a non-blank `Authorization` value — the same `string.IsNullOrWhiteSpace` test the JWT `OnMessageReceived` handler uses, so a blank header cannot disarm the guard while the cookie still authenticates.
- **Antiforgery client contract**: the request token from `GET /auth/antiforgery` is bound to the principal it was issued for, so clients must fetch it *after* logging in and re-fetch it after every login, logout, or account switch. A token obtained while anonymous is rejected with 403 once the caller holds an auth cookie.
- `SecurityHeadersMiddleware`: locked-down CSP and friends on every response.
- Rate limiting: `"auth"` policy (5 req/min per IP) on register/login/refresh/change-password, plus a global 100 req/min limiter; 429 with `Retry-After`.
- Authorization: role policies in `Security/Policies.cs` plus a fallback policy requiring authentication — endpoints are secure by default; opt out explicitly with `AllowAnonymous`.

Middleware order in `Program.cs` matters (SecurityHeaders → ExceptionHandler → HSTS/HTTPS → CORS → RateLimiter → AuthN → CsrfGuard → AuthZ); insert new middleware deliberately.

## Testing

xUnit + FluentAssertions everywhere; `Xunit` is a global using in test projects. Naming: `Method_Condition_ExpectedResult`. No mocking libraries — Application tests use hand-written fakes in `tests/BuildCv.Application.Tests/Fakes/`; extend those instead of adding Moq/NSubstitute.

API tests use `ApiTestFactory` (`WebApplicationFactory<Program>`), which forces the Development environment and injects in-memory `Jwt:*` config. Use its `CreateCookieClient()` and the `TestHelpers` register/login extensions for authenticated scenarios.

## Conventions

- `Nullable` and `ImplicitUsings` enabled everywhere; Domain/Application/Infrastructure build with `TreatWarningsAsErrors` (Api and tests currently don't).
- Almost every type is `sealed`; prefer private constructor + static factory over public constructors.
- File-scoped namespaces, `var` preferred, expression-bodied members and switch expressions preferred (see `.editorconfig`).
- Conventional commits scoped by layer (`feat(domain): ...`, `feat(infrastructure): ...`); PRs are merged per layer/feature slice.
