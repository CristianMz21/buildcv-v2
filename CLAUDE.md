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

CI (`.github/workflows/ci.yml`) runs two parallel jobs: `build-and-unit-test` (restore → build Release → `dotnet format --verify-no-changes` → unit tests with coverage) and `integration-test` (restore → build Release → integration tests with coverage, 15-minute timeout). Unformatted code fails CI, so run `dotnet format` before committing.

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
- **Infrastructure** (`src/BuildCv.Infrastructure`): `DependencyInjection.AddInfrastructure(configuration, environmentName)` is the single registration point for all handlers and adapters. Persistence is EF Core on SQL Server (`Persistence/EfCore/`); `Persistence:Provider` switches to the in-memory repositories, which are refused outside Development unless `Persistence:AllowInMemoryOutsideDevelopment` is set, and always in Production. With no `ConnectionStrings:BuildCv` configured, Development falls back to `BuildCvDbContextFactory.DefaultConnectionString` (which matches `docker-compose.yml`). `JwtSettings.SigningKey` must be ≥32 chars (validated at startup).
- **Api** (`src/BuildCv.Api`): endpoints grouped per feature as `Map*Endpoints()` extensions in `Endpoints/`; request/response DTOs are sealed records in `Contracts/`, mapped to Application commands inside the endpoint lambdas — never reuse Application types as wire contracts. `Program.cs` ends with `public partial class Program;` for the test factory.

### Error handling — three-tier convention

Follow this strictly; don't mix tiers:

1. **Domain** throws typed `DomainException` subclasses for invariant violations.
2. **Application** handlers catch `DomainException`/`ArgumentException` and return `Result<T>` (`Domain/Common/ValueObjects/Result.cs`, with `Map`/`Bind`/`Match`).
3. **Api** converts `Result<T>` to HTTP via `Common/ResultExtensions.ToHttpResult()` (403 for "Forbidden.", 404 for errors ending in "not found.", else 400); anything that leaks is turned into RFC 7807 ProblemDetails by the `IExceptionHandler`s in `Common/ApiExceptionHandlers.cs`. All error responses are ProblemDetails-shaped.

### List queries — keyset pagination

**There are no unbounded list methods on any repository port, and adding one back is a regression.** Every list is `GetPage*Async(key, PageRequest, ct)` returning `Page<T>` (`Application/Common/Pagination/`).

- `PageRequest.Create(limit, cursor)` clamps the limit into 1..100 (default 20) and **validates** the cursor, returning `Result<PageRequest>`; a cursor that will not decode fails with `PageRequest.InvalidCursorError` and becomes a 400. It never falls back to the first page — that would silently restart a client's walk.
- `Cursor` wraps one number, the shadow `Seq` of the last row delivered, base64url-encoded as eight big-endian bytes so a malformed value is rejected on length alone. `Cursor.TryParse` calls `Base64Url.IsValid` **before** `TryDecodeFromChars`, because the latter throws `FormatException` on a bad character despite the `Try` in its name.
- Repositories fetch `Limit + 1` rows and hand the probe to `Page<T>.From`, the single copy of the boundary arithmetic. The next cursor is the position of the **last row actually returned**, never the probe row.
- EF paths go through `KeysetQueryExtensions`; the cursor comparison is on `EF.Property<long>(e, ShadowColumns.Seq)`, which is why this translates at all — value-converted strongly-typed ids do not translate `<`/`>`. `KeysetQueryTranslationTests` reads the generated SQL without a database so a client-evaluation fallback cannot hide behind green page assertions.
- The in-memory store and the Application fakes carry an insertion counter standing in for `Seq` and share `KeysetSequence`, so they page identically to SQL Server. Api tests run on the in-memory provider; if it drifted, they would certify behavior production does not have.
- Score history (`IAnalysisRepository`) pages **oldest first**; everything else is newest first. The cursor comparison flips with the direction.

### Encrypted columns

PII columns are AES-GCM sealed and stored as `varbinary` (`Persistence/Converters/EncryptedConverter.cs`). **Never query them in LINQ** — the envelope carries a fresh nonce on every write, so two rows holding the same value have different bytes: `Where(a => a.Email == email)` compiles, runs, and returns nothing forever, and no index on the column can enforce uniqueness either.

Exact-match lookups go through the HMAC **blind-index** shadow columns instead — `EmailHash` on `identity.Accounts`, `TokenHash` on `identity.RefreshTokens` — which carry the (filtered, unique) indexes. Rules:

- Digests are computed only through `Persistence/BlindIndexes/AccountEmailIndex` and `RefreshTokenIndex`, never through `IBlindIndex` directly (they own the AAD context string and demand an already-normalized value).
- **Writes** use `Compute` (active key only) and happen exclusively in `BlindIndexSaveChangesInterceptor`, so the digest and the ciphertext can never disagree.
- **Reads** use `ComputeCandidates` (every configured key) via `Persistence/EfCore/BlindIndexLookup`, which takes a candidate list and has no single-digest overload. `Compute` on a read path silently answers "not found" for every row written under a retired key during a rotation window — which also lets the same address register twice.

Analytical columns stay plaintext by design (skill names, levels, years, date ranges, scores): they are what the scoring engine and internal analytics query, and no query can reach through an envelope. `Persistence/Configurations/*.cs` states the classification per property; `ModelConfigurationTests` asserts it.

Soft delete is a shadow `DeletedAt` plus a global query filter on every aggregate root. `Account.Delete()` and `Organization.Delete()` set a domain `Status` **and** the repository writes the tombstone alongside it — one observable delete, and the filtered unique indexes then genuinely free the address or slug for re-registration.

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

Tests tagged `[Trait("Category", "Integration")]` require a running local Docker daemon: they start and migrate their own disposable SQL Server 2022 container via `Testcontainers.MsSql` (not the `docker-compose.yml` instance — no `docker compose up` needed). `dotnet test --filter "Category!=Integration"` runs unit tests only; `--filter "Category=Integration"` runs integration tests only. `docker-compose.yml` is unrelated — it's for manual development and `dotnet ef database update`.

## Conventions

- `Nullable` and `ImplicitUsings` enabled everywhere; Domain/Application/Infrastructure build with `TreatWarningsAsErrors` (Api and tests currently don't).
- Almost every type is `sealed`; prefer private constructor + static factory over public constructors.
- File-scoped namespaces, `var` preferred, expression-bodied members and switch expressions preferred (see `.editorconfig`).
- Conventional commits scoped by layer (`feat(domain): ...`, `feat(infrastructure): ...`); PRs are merged per layer/feature slice.
