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

Two deliberate qualifications to "all error responses are ProblemDetails-shaped":

- `RouteHandlerOptions.ThrowOnBadRequest` is on **in every environment** (not its `IsDevelopment()` default), so binding failures — malformed JSON, a bare `null` body, `MaxDepth` — reach `MalformedRequestExceptionHandler` and come back as ProblemDetails. Left at the default, the same input answered an empty 400 in production and a logged 500 in Development.
- The **413 is not ProblemDetails-shaped and cannot be made so**: Kestrel enforces `IRequestSizeLimitMetadata` and tears the connection down inside the server before any `IExceptionHandler` runs. Measured, with `ThrowOnBadRequest` both off and on. Do not add middleware to shape it — that produces two different bodies for one class of refusal (see the remarks on `MalformedRequestExceptionHandler`).
- A **malformed (unterminated) multipart body on `POST /resumes/import/extract`** is also a bare 400 — the second unshaped response. Measured: minimal-API `IFormFile` binding swallows the multipart reader's `IOException` into an empty 400 that never reaches an `IExceptionHandler`, so `ThrowOnBadRequest` cannot redirect it. Shaping it means abandoning `IFormFile` for a manual `ReadFormAsync`, which — also measured — turns that route's torn-down 413 into a catchable, shaped one; that is a change to confirmed size-enforcement behavior not worth making for a framing 400. Pinned by `ResumeExtractTests.Extract_WithAnUnterminatedMultipartBody_IsABare400`. (A *well-formed* multipart with no `file` part is different: that binding failure IS a `BadHttpRequestException` and comes back shaped.)

### Resume import — the field-error path

`POST /resumes/import` (`Application/Resumes/CreateResumeFromDraft.cs`) is the one use case that does not fit `Result<T>`'s single error string: a reviewed draft carries forty-plus fields, so failures are collected **all in one pass** and keyed by field path (`experience[2].endDate`), surfacing as the `errors` object of a ProblemDetails 400 — the same shape ASP.NET model validation emits. Rules that keep it honest:

- **`ResumeDraft` is all nullable strings, on purpose.** It holds untrusted extracted text; typing a field would move parsing to the binding boundary, where a failure cannot carry a field path or collect siblings.
- **Validation and construction are the same pass** (`ResumeDraftValidator`): every verdict comes from calling the real Domain factory and catching what it threw. Do not add a check-first-build-later validator beside it — two statements of one rule is how they diverge.
- **All-or-nothing, one `AddAsync`.** Nothing is persisted if any field fails; `FakeResumeRepository.WriteCount` pins the single write.
- **Error messages name positions, never values.** The duplicate-entry messages on `Resume` say `"Duplicates the skill at index 0."` because `Certificate.Name`/`Interest.Name` are encrypted at rest and the old messages echoed them back in plaintext. The field path already carries the later index; the message carries the earlier one.
- **Bounded plaintext columns need a Domain rule** (`Language.Name` ≤ 100 is the precedent). A value that reaches SQL Server too long is error 2628, translated by `SaveChangesExtensions` to `ValueTooLongException` (400) **with the inner exception deliberately dropped** — SQL Server's own message quotes the offending value, and attaching it would put candidate text in the error log. The translation is the net; the Domain rule is the fix.
- **Throttled per account, not per IP** (`Security/ResumeImportRateLimiter`, acquired inside the endpoint like `PasswordChangeRateLimiter` and for the same reason). An accepted import is the most durable write in the API — one request can create ~9,000 owned rows that load eagerly forever after. The body ceiling is 2 MiB via `RequestSizeLimitAttribute` metadata, which **the framework enforces on its own** for minimal APIs, chunked bodies included — measured; do not reintroduce a middleware for it.

### Readability — the second score, and why it is a second aggregate

The README promises "puntaje determinista de coincidencia **y legibilidad**". `Analysis` is the *coincidencia* half; `Domain/Readability/ReadabilityReport` is the *legibilidad* half, and it is a structural sibling of `Analysis` rather than a part of it. Three concrete blockers, not a preference:

1. **`Analysis` requires a non-nullable `JobPostingId`.** Readability must answer with zero job offers in the system, which is the whole point of it — a candidate gets value the moment they upload a CV. Making that column nullable would leave one append-only fact table meaning two different things.
2. **`ScoreBreakdown.Sections` projects `Enum.GetValues<SectionType>()` and `ScoreFor` throws on an unknown member**, so that enum is effectively closed at six for every persisted row: appending to it breaks stored breakdowns at *read* time. Hence `ReadabilitySectionType` (`Completeness`, `Contact`, `Achievements`, `Chronology`, `AtsParseability`) and a parallel `ReadabilityRecommendation`.
3. **The weights sum-to-1.0 invariant** is what makes each `WeightedTotal` a 0–1 number, which makes each total a percentage, which gives the bands meaning. Two weightings, two `SchemaVersion`s, two bump rules. **Never blend the two totals server-side** — the readability total is named `ReadabilityScore` and `OverallScore` keeps meaning only "match against this posting". A combined display is the client's business.

- **`Impact` is measured, never estimated**, exactly as in `RecommendationBuilder`: it is the section's weight times the delta from re-evaluating the *same* `ReadabilityRules` formula with one gap closed, and `Priority` is a pure function of it. `ActingOnAReadabilityRecommendationTests` builds a report, applies exactly the fix one recommendation names, re-evaluates and asserts the delta equals `Impact` within 1e-9 — one test per rule.
- **The four measurable sections read disjoint inputs, and that is a constraint rather than an accident.** `Impact` is one section's weight times that section's delta, which equals the total delta only when the fix moves exactly one section. That is why the *work-history heading* belongs to Chronology (which already reads the entries) and not to Completeness: counted in both, adding a first experience would move two sections and every `Impact` naming it would understate what it paid.
- **`AtsParseability` renormalizes out of every report this build can produce.** It grades the uploaded *document*, and the signed import-signals evidence it needs is a separate change; `ReadabilityEngine` passes `hasImportSignals: false` and `RenormalizedTo` drops the section, so the ceiling stays 1.00. **Whoever lands the evidence must land the score and the applicability together** — applicability alone would give the section its 0.10 weight against a hard zero and cap every candidate at 0.90, which is the failure `ScoringWeightsSnapshot.RenormalizedTo`'s remark on Languages describes.
- **Emit nothing a candidate cannot act on.** There is no "your career is too short" rule, and a resume with no experience entries gets no Achievements advice at all — the section still scores zero and still carries its weight, but "add a bullet point" names an edit to a role that does not exist. The advice appears once the work history does.
- `readability.Recommendations.Message` is encrypted **under its own context string** (`ReadabilityRecommendation.Message`), never `Recommendation.Message`: the context is the AAD, so sharing one would let an envelope move between the two recommendation tables and still decrypt. `SchemaRoundTripTests` executes that move and asserts it fails.
- `ResumeRepository.DeleteAsync` cascades to readability reports as well as to analyses, and every future aggregate keyed by `ResumeId` must be added there too — there is no foreign key for the engine to cascade through, and a readability message quotes the candidate's own bullet points and job titles.

### List queries — keyset pagination

**There are no unbounded list methods on any repository port, and adding one back is a regression.** Every list is `GetPage*Async(key, PageRequest, ct)` returning `Page<T>` (`Application/Common/Pagination/`).

- `PageRequest.Create(limit, cursor)` clamps the limit into 1..100 (default 20) and **validates** the cursor, returning `Result<PageRequest>`; a cursor that will not decode fails with `PageRequest.InvalidCursorError` and becomes a 400. It never falls back to the first page — that would silently restart a client's walk.
- `Cursor` wraps one number, the shadow `Seq` of the last row delivered, base64url-encoded as eight big-endian bytes — always exactly 11 characters. `TryParse` gates on that length **first** (`Base64Url.IsValid` alone tolerates embedded whitespace and padding, so `"AAAAAAAAACo="` and `" AAAAAAAAACo"` would otherwise be accepted aliases of position 42), then calls `Base64Url.IsValid` **before** `TryDecodeFromChars`, because the latter throws `FormatException` on a bad character despite the `Try` in its name. The token is **not table-scoped**: it carries a bare position, so a cursor minted on one list is accepted on another and yields a valid-but-meaningless page.
- Repositories fetch `Limit + 1` rows and hand the probe to `Page<T>.From`, the single copy of the boundary arithmetic. The next cursor is the position of the **last row actually returned**, never the probe row.
- EF paths go through `KeysetQueryExtensions`; the cursor comparison is on `EF.Property<long>(e, ShadowColumns.Seq)`, which is why this translates at all — value-converted strongly-typed ids do not translate `<`/`>`. `KeysetQueryTranslationTests` reads the generated SQL without a database so a client-evaluation fallback cannot hide behind green page assertions.
- **The probe is `AsSplitQuery`, and removing that is a regression `TOP` will not catch.** `Resume` owns ten collections, `JobPosting` three and **`Analysis` one** — that last one is the entry to keep, because `Analysis` owned none when the probe was written, and "score history pages a collection-free entity" is exactly the belief that would have let a page of twenty fan out the moment `Recommendations` was mapped, with no code in `KeysetQueryExtensions` changing. Owned navigations load eagerly, so in one statement they become a LEFT JOIN each onto the same principal and the server returns their cartesian product. `TOP` caps the principals *inside* the subquery, so the fan-out happens outside it — rows shipped is the sum, over the page, of the **product** of each principal's collection counts, and nothing caps any collection. EF de-duplicates on materialization, so every page-shape assertion passes either way; `KeysetQueryTranslationTests` asserts the **join count**, and asserts the single-query form still joins once per collection so that zero is evidence rather than a tautology. `ResumeRepository.GetByIdAsync` splits for the same reason and is pinned by `ResumeQueryTranslationTests`. The price is that a page is not one atomic read.
- The in-memory store and the Application fakes carry an insertion counter standing in for `Seq` and share `KeysetSequence`, so they page identically to SQL Server. Api tests run on the in-memory provider; if it drifted, they would certify behavior production does not have.
- Score history (`IAnalysisRepository`) pages **oldest first**; everything else is newest first. The cursor comparison flips with the direction.

### Encrypted columns

PII columns are AES-GCM sealed and stored as `varbinary` (`Persistence/Converters/EncryptedConverter.cs`). **Never query them in LINQ** — the envelope carries a fresh nonce on every write, so two rows holding the same value have different bytes: `Where(a => a.Email == email)` compiles, runs, and returns nothing forever, and no index on the column can enforce uniqueness either.

Exact-match lookups go through the HMAC **blind-index** shadow columns instead — `EmailHash` on `identity.Accounts`, `TokenHash` on `identity.RefreshTokens` — which carry the (filtered, unique) indexes. Rules:

- Digests are computed only through `Persistence/BlindIndexes/AccountEmailIndex` and `RefreshTokenIndex`, never through `IBlindIndex` directly (they own the AAD context string and demand an already-normalized value).
- **Writes** use `Compute` (active key only) and happen exclusively in `BlindIndexSaveChangesInterceptor`, so the digest and the ciphertext can never disagree.
- **Reads** use `ComputeCandidates` (every configured key) via `Persistence/EfCore/BlindIndexLookup`, which takes a candidate list and has no single-digest overload. `Compute` on a read path silently answers "not found" for every row written under a retired key during a rotation window — which also lets the same address register twice.

Analytical columns stay plaintext by design (skill names, levels, years, date ranges, scores): they are what the scoring engine and internal analytics query, and no query can reach through an envelope. `Persistence/Configurations/*.cs` states the classification per property; `ModelConfigurationTests` asserts it.

**"A level" means the closed enum, not prose about one.** `Language.Level` and `Education.Level` are plaintext because the engine compares them; the free-text column beside each — `Language.Fluency`, `Education.Degree`, `Education.Grade` — is **encrypted**, because it is a sentence a candidate wrote about themselves and someone can type *"nativo, aprendido de mi abuela colombiana"* into it. `Fluency` was the exception until PR #16 made `Level` the scoring input and forbade the engine from reading `Fluency` at all; sealing it then cost no query, which is what made the ruling cheap. The test for the plaintext side is not "does this describe a level" but **"does something actually query it"**.

Soft delete is a shadow `DeletedAt` plus a global query filter on every aggregate root. `Account.Delete()` and `Organization.Delete()` set a domain `Status` **and** the repository writes the tombstone alongside it — one observable delete, and the filtered unique indexes then genuinely free the address or slug for re-registration.

### API security model

Auth is JWT in HttpOnly cookies (with `Authorization: Bearer` fallback — `OnMessageReceived` reads the `access_token` cookie). Refresh tokens are opaque, cookie-scoped to `/auth/refresh`. Cross-cutting pieces live in `src/BuildCv.Api/Security/`:

- **Session termination**: clearing cookies is not logout. `IRefreshTokenRepository.RevokeAllForAccountAsync` is the server-side half, driven by `Application/Identity/RevokeSessions.cs`. `POST /auth/logout` and a successful `POST /auth/change-password` both revoke every refresh token on the account; without that, a refresh cookie captured earlier keeps minting access tokens for the full 30-day lifetime. Three consequences to keep in mind: logout is *log out everywhere* (the refresh cookie is scoped to `/auth/refresh`, so no other endpoint can tell which token belongs to the caller); access tokens already issued stay valid until they expire, so revocation bounds the attacker's window to one access-token lifetime rather than closing it instantly; and a stolen access token is therefore also a one-request "log the victim out everywhere" capability — inherent to revoke-all, and strictly less than what that token already grants.
- **Logout authenticates against a second JWT scheme.** `AuthenticationSchemes.ExpiredAccessTokenAllowed` is identical to the default scheme except `ValidateLifetime = false`, and `/auth/logout` reaches it through an explicit `AuthenticateAsync` call. No authorization policy names it and it is not the default, so an expired token still opens nothing else. This exists because the usual logout is "idle tab, token already expired": validating lifetime there would reduce logout to clearing cookies and revoking nothing, which is the bug the endpoint exists to close.
- **The access-token cookie is a persistent 30-day cookie**, sharing the refresh token's expiry (`AuthCookies.SetTokens`). It is *not* a session cookie, and it deliberately outlives the JWT it carries — the browser used to delete the only credential naming the account at the same instant the token went stale, which is what made logout unable to revoke. The JWT's `exp` remains the security control on every scheme except the logout-only one. Two accepted costs:
  - A stale JWT sits on disk for up to 30 days, and JWTs are signed but **not encrypted** — `sub`, `email` and `role` are readable by anyone with filesystem access for that month. The refresh cookie is already a 30-day on-disk artifact granting strictly more capability, so this is not a new class of exposure.
  - **The 401 → 403 flip, repo-wide.** See the client contract below.
- `/auth/logout` is `AllowAnonymous` so a caller with no usable credential can still clear its cookies, and carries its own `RateLimitPolicies.Logout` window (20/min per IP) because authentication used to be its de facto throttle. It is still CSRF-guarded and must stay out of `CsrfGuardMiddleware.ExemptPaths`. On a revocation **failure** it answers 500 and leaves the cookies in place: clearing them would take away the credential needed to retry while the session is genuinely still live in the store. A failed logout therefore leaves that browser armed — deliberate, because a store that cannot revoke is exactly when a false "you are logged out" is most dangerous.

- `CsrfGuardMiddleware`: double-submit-cookie CSRF check (`X-XSRF-TOKEN` header) for unsafe methods on cookie-authenticated requests only; bearer requests are exempt by design. "Bearer request" means a non-blank `Authorization` value — the same `string.IsNullOrWhiteSpace` test the JWT `OnMessageReceived` handler uses, so a blank header cannot disarm the guard while the cookie still authenticates.
- **Antiforgery client contract**: the request token from `GET /auth/antiforgery` is bound to the principal it was issued for, so clients must fetch it *after* logging in and re-fetch it whenever that principal changes — login, logout, account switch, **and access-token expiry**. The binding is rejected with 403 in both directions: a token obtained while anonymous fails once the caller holds a valid auth cookie, and a token obtained while authenticated fails once the access token has expired and the caller reads as anonymous again. The contract is also stated on the endpoint itself and in its OpenAPI description, which is where a client developer will look.
- **Clients must refresh proactively, not on 401.** Because the access-token cookie outlives the JWT and `CsrfGuardMiddleware` gates on cookie *presence*, an idle cookie client's next `POST`/`PUT`/`DELETE`/`PATCH` to **any** non-exempt route enters antiforgery validation as an anonymous principal holding an authenticated-bound token and answers **403 "CSRF validation failed."** — where the pre-existing behaviour was **401** with no cookie sent at all. This applies repo-wide, not just to `/auth/logout`, and it breaks the reactive "on 401, call `/auth/refresh`, retry" loop. Schedule the refresh off the `expiresIn` field returned by `/auth/login` and `/auth/refresh`, then re-fetch the antiforgery token. Two `SessionTerminationTests` pin the two halves: `Login_AccessCookieOutlivesTheAccessTokenAndMatchesTheSession` (the cookie survives its JWT) and `StaleAccessTokenCookie_WithAuthenticatedBoundAntiforgeryToken_Returns403` (one token, accepted with a live cookie and rejected with a stale one, so the 403 is the binding mismatch and not a missing token). `CsrfGuardMiddleware` itself is unchanged — what is new is that clients now reach this state. A session cookie would not avoid the flip either — an idle but open browser hits it too — so it is inherent to the cookie outliving the JWT, which is what makes logout able to revoke at all.
- `SecurityHeadersMiddleware`: locked-down CSP and friends on every response.
- Rate limiting: `"auth"` policy (5 req/min per IP) on register/login/refresh, `"logout"` (20/min per IP) on logout, plus a global 100 req/min limiter; 429 with `Retry-After`. Partition keys come from `Security/RateLimitPartitions.ClientKey`: IPv4-mapped IPv6 is normalized to plain IPv4, **IPv6 is truncated to its /64** (one customer is routinely delegated a whole /64, so keying on the full /128 would let them mint 2^64 buckets and walk through every limiter), and a missing peer address collapses to one shared `"unknown"` bucket on purpose, so it fails closed. `Security/ClientAddress.Describe` is the full-precision counterpart used by `AuditLog` — same normalization, no truncation, because forensics wants the exact address the limiter charged the allocation for.
  - The /64 truncation is argued above as attacker control, but it cuts both ways: a corporate LAN with one /64 per VLAN now shares a single 5/min auth window across that VLAN. That is deliberate parity with how IPv4 NAT already behaves, not an oversight — and it is the reason `/auth/change-password` is partitioned per account instead.
  - The 20/min logout ceiling assumes `RevokeAllForAccountAsync` is cheap: a dictionary scan in the in-memory store, and in EF a `RemoveRange` over the filtered index on `AccountId` (`RefreshTokenConfiguration`). It goes through `Remove()` rather than `ExecuteDeleteAsync` deliberately — the latter bypasses `SaveChanges`, so the audit interceptor never writes the `DeletedAt` tombstone and revocation silently becomes a hard delete. Re-size this window if that ever becomes a table scan; 20 unauthenticated table scans a minute per IP is not the same bargain.
- `/auth/change-password` is throttled **per account** by `Security/PasswordChangeRateLimiter`, acquired inside the endpoint rather than as a named policy — `UseRateLimiter` runs before `UseAuthentication`, so a policy partitioner has no principal to key on. Sharing the per-IP auth window let one client behind a NAT deny password rotation to everyone on that address, while buying nothing against an attacker who already holds an access token and can rotate source IPs.
- Authorization: role policies in `Security/Policies.cs` plus a fallback policy requiring authentication — endpoints are secure by default; opt out explicitly with `AllowAnonymous`.

Middleware order in `Program.cs` matters (ForwardedHeaders → **CorrelationId** → SecurityHeaders → ExceptionHandler → HSTS/HTTPS → CORS → RateLimiter → AuthN → CsrfGuard → AuthZ); insert new middleware deliberately.

### Observability — correlation id

`CorrelationIdMiddleware` (`Api/Observability/`) gives every request one id, echoes it as `X-Correlation-ID`, and opens an `ILogger` scope keyed `CorrelationId` around the rest of the pipeline.

- **It sits before `UseExceptionHandler`**, because the lines most worth correlating are the ones the `IExceptionHandler`s write — a 500 the caller was handed an id for is the difference between "a user reports an error" and "here is the request". Nothing above it logs.
- **The echo is written from `Response.OnStarting`**, the same lesson as `SecurityHeadersMiddleware`: `ExceptionHandlerMiddleware` clears the response, so an eagerly assigned header is gone on exactly the responses that need one.
- **An inbound value is adopted only if it is safe to log**: 1–64 characters of ASCII letters, digits and hyphen. Anything else — a space, a quote, a brace, a comma, a tab, 65 characters, or the header sent twice (`StringValues.ToString()` joins with a comma) — is **replaced** with a generated `Guid("N")`, never trimmed or stripped. Trimming would alias two clients' ids onto one string; stripping would report an id nobody sent while looking like the one they did.
- The scope covers everything downstream, framework loggers included — `ILoggerFactory` shares one `IExternalScopeProvider`. Hosting's own "Request starting/finished" lines are the exception and always will be: `HostingApplication` wraps the pipeline from outside.
- `CorrelationIdTests.EveryLineARequestWrites_CarriesThatRequestsCorrelationId` drives **two** requests and checks each one's lines for its own id *and against the other's* — an id attached once and never cleared would satisfy a single-request assertion with the wrong value.

### Observability — health probes

`GET /health/live` and `GET /health/ready` (`Api/Health/`), both **outside `/v1`**: a probe URL lives in a deployment manifest, not a client library, so it must not move when the product contract versions.

- **Liveness touches nothing outside the process** (`Predicate = _ => false`, no checks selected). A failed liveness probe *restarts* the process, so a liveness check that opened a database connection would roll-restart the fleet the moment the database hiccuped — at the moment it can least afford a reconnection stampede. `HealthEndpointTests.Live_ConsultsNoProbeAtAll_WhileReadyConsultsItEveryTime` **counts probe calls** rather than reading a status code, because 200 is a small closed value that a skipped check and a succeeding check produce identically.
- **Readiness is tag-filtered** on `DatabaseHealthCheck.ReadinessTag`, so a newly registered check does not silently join it. Its one check goes through `IPersistenceProbe` (`Application/Common/Services/`) — registered on **both** persistence branches (`EfCorePersistenceProbe` / `InMemoryPersistenceProbe`), because a missing registration must not be able to answer "ready".
- Both are `AllowAnonymous`, `DisableRateLimiting` (the **global** 100/min limiter is the one that would fire; a 429 is not a health status, so a throttled probe reads as a failed one) and constrained to **GET** — `MapHealthChecks` maps every method by default, and an unsafe method on an anonymous path would put `CsrfGuardMiddleware` in front of a route that changes nothing.
- The default plaintext writer emits the **status only**, so health-check descriptions never reach the wire — but `HealthCheckService` logs every description, which is why `DatabaseHealthCheck`'s are fixed strings and never quote the store (a SQL Server connection failure names host, database and sometimes login).

### Observability — metrics and spans, no exporter

Instruments only: `Meter`, `Counter<T>` and `ActivitySource` are all in the BCL on `net10.0` (`System.Diagnostics.DiagnosticSource`, part of `Microsoft.NETCore.App`), so **zero packages were added**. The OpenTelemetry SDK and its exporters are deliberately deferred — they configure a collector that does not exist. `StartActivity` returns `null` with no listener attached, so today every span costs one null check.

`Application/Common/Observability/` holds one `Meter` named **`BuildCv`** (`BuildCvMetrics`) and one `ActivitySource` named **`BuildCv`** (`BuildCvActivities`). An exporter must also name **`BuildCv.Infrastructure.Encryption`**, the pre-existing meter behind `buildcv.encryption.operations` — meter names match exactly.

| Instrument | Tag | Values |
|---|---|---|
| `buildcv.scoring.runs` | `outcome` | `computed`, `deduplicated` (`ScoringOutcomes`) |
| `buildcv.readability.reports` | — | untagged; no de-duplication, no variants |
| `buildcv.documents.extraction_failures` | `reason` | 10 values (`DocumentExtractionFailureReasons`) |
| `buildcv.throttle.rejections` | `policy` | 6 values (`ThrottlePolicies`) |

- **A tag is a time-series dimension and is covered by none of this repo's encryption.** No account id, resume id, partition key, skill name or anything else derived from candidate text may appear in one — it would make a metrics backend an unencrypted PII store *and* multiply the series count by the number of users. Every emit method takes a value from a closed set named in code, and there is no overload accepting caller-supplied dimensions.
- `BuildCvMetrics` is **an instance, not a static**, and stamps its `Meter` with `scope: this`. That is the `IMeterFactory` mechanism, minus the `AddMetrics()` call that lives in an ASP.NET Core assembly Application and Infrastructure cannot reference. It makes a `MeterListener` able to tell one composed host's measurements from another's — without it, an assertion could be satisfied by a measurement some *other* test produced.
- The extraction reason is named **at the site that writes the message**, so the tag and the prose are one statement. `DocumentTextExtractionTests.TheReasonsThisSuiteEmits_...` checks the set in both directions — everything emitted is declared, and everything declared is reachable except `password_protected`, which needs a genuinely encrypted PDF fixture and is named as the gap rather than quietly missing.
- `buildcv.throttle.rejections` is emitted from **two** places: `RateLimiterOptions.OnRejected` for the middleware's limiters, and inside the endpoint for the three per-account limiters, which the middleware has already waved through by the time they refuse. `OnRejected` reads the policy from endpoint metadata because `OnRejectedContext` exposes neither the middleware's internal global-vs-endpoint flag nor a policy name; that is exact for every route without a policy and reports the endpoint's policy on the four that have one even in the rare case where the global 100/min ceiling fired first.
- Spans: `buildcv.document.extract` (tags `buildcv.document.format`, `buildcv.document.outcome`), `buildcv.resume.score` (tag `buildcv.scoring.outcome`), `buildcv.resume.readability` (no tags at all — everything about a readability run is either an identifier or derived from advice that quotes the candidate's own bullet points). The **declared `Content-Type` never reaches a span**: it is client-controlled, so `DocumentFormats.Of` maps it into a closed set first.
- `BuildCvActivities` **is** static, unlike the metrics: a span is attributed by parentage (`Activity.Current` is an AsyncLocal), so a test isolates its own spans by trace id. A measurement carries no parent, which is why only the meter needs a scope.

### Deployment requirement — forwarded headers

Rate limiting partitions on `Connection.RemoteIpAddress`. **Behind any reverse proxy, ingress, or CDN you must configure `Network:ForwardedHeaders`**, otherwise every client collapses into the proxy's single partition and the 5/min auth window becomes a global 5/min cap for the whole deployment — a self-inflicted denial of service that also throttles no individual attacker.

It is **off by default and must stay that way for direct-exposure deployments**: `X-Forwarded-For` is client-controlled, so an unrestricted `UseForwardedHeaders` lets any caller claim a new source address per request and defeat rate limiting entirely — worse than the collapsed partition. Enabling it therefore requires naming the proxies; `ForwardedHeadersConfiguration.Build` throws at startup if the allowlist is empty, and only `X-Forwarded-For`/`X-Forwarded-Proto` are honoured (never `Host`).

```json
"Network": {
  "ForwardedHeaders": {
    "Enabled": true,
    "KnownProxies": ["10.20.30.5"],
    "KnownNetworks": ["10.20.30.0/29"],
    "ForwardLimit": 1
  }
}
```

Prefer `KnownProxies` alone whenever the proxy address is stable — it is the narrowest thing you can write. Reach for `KnownNetworks` only for an autoscaling proxy tier, and size it to that tier, not to the site: on a flat internal network `"10.0.0.0/8"` would trust every internal client to set its own `X-Forwarded-For`, which is the failure this setting exists to prevent. Keep `ForwardLimit` equal to the real hop count between the client and Kestrel.

### Deployment requirement — two migrations that destroy data

`20260802051841_EncryptLanguageFluency` **drops every stored `resumes.Languages.Fluency` value**, in both directions. That is deliberate and it is the cheaper of two bad options. `Fluency` predates this chain (it ships in `InitialCreate` and is on `main`), so real rows hold plaintext, and the scaffolded `AlterColumn nvarchar(50) → varbinary(max)` would have kept those bytes: SQL Server accepts the conversion, the result is raw UTF-16, and `AesGcmFieldEncryptor.Decrypt` rejects it on the version byte. `Fluency` is an eagerly-loaded owned property, so that is not a lost field — it is a **resume that no longer loads**. Encrypting in place is not available to a migration (the key ring is configuration and SQL Server has no AES-GCM), so the column is dropped and re-added empty. What is lost is display-only free text no scoring path reads; candidates can retype it. Announce it before deploying.

`20260801140223_AddSectionScoringAndRecommendations` is **forward-only in practice, and the reason is data loss rather than anything you can work around**. Its `Down()` restores the pre-chain schema faithfully and still destroys `scoring.Recommendations` in full plus every `Analyses.LanguagesScore`. Advice is derived, so re-scoring produces advice again — but against today's resume and posting, not the ones the historical analysis was taken against, so the score history stops being explainable by the recommendations beside it. Plan the deploy knowing there is no rolling back past the first write; the full reasoning is on the migration's `Down()`.

The scaffolded `Down()` was worse than that: it restored `Analyses.Recommendations` as `nvarchar(max) NOT NULL DEFAULT ''`, and the pre-chain mapping parsed that column with `JsonSerializer.Deserialize<string[]>`, which **throws on `''`** — so a rollback made every `Analysis` row unreadable, including rows written long before this chain. The default is now `'[]'`, which that same converter parses to an empty list. If you ever hand-edit a generated `Down()`, check the default against the converter that will read it, not against the column type.

The `ScoringWeightsSnapshot` weights-JSON cliff documented in `Domain/Scoring/ScoringWeightsSnapshot.cs` is a **different and currently unreachable** hazard: it needs a posting that states a language requirement, and nothing in the shipped API can create one.

## Testing

xUnit + FluentAssertions everywhere; `Xunit` is a global using in test projects. Naming: `Method_Condition_ExpectedResult`. No mocking libraries — Application tests use hand-written fakes in `tests/BuildCv.Application.Tests/Fakes/`; extend those instead of adding Moq/NSubstitute.

API tests use `ApiTestFactory` (`WebApplicationFactory<Program>`), which forces the Development environment and injects in-memory `Jwt:*` config. Use its `CreateCookieClient()` and the `TestHelpers` register/login extensions for authenticated scenarios.

Tests tagged `[Trait("Category", "Integration")]` require a running local Docker daemon: they start and migrate their own disposable SQL Server 2022 container via `Testcontainers.MsSql` (not the `docker-compose.yml` instance — no `docker compose up` needed). `dotnet test --filter "Category!=Integration"` runs unit tests only; `--filter "Category=Integration"` runs integration tests only. `docker-compose.yml` is unrelated — it's for manual development and `dotnet ef database update`.

## Conventions

- `Nullable` and `ImplicitUsings` enabled everywhere; Domain/Application/Infrastructure build with `TreatWarningsAsErrors` (Api and tests currently don't).
- Almost every type is `sealed`; prefer private constructor + static factory over public constructors.
- File-scoped namespaces, `var` preferred, expression-bodied members and switch expressions preferred (see `.editorconfig`).
- Conventional commits scoped by layer (`feat(domain): ...`, `feat(infrastructure): ...`); PRs are merged per layer/feature slice.
