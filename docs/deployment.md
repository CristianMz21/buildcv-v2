# Deploying BuildCv

For whoever puts this in front of real people. It assumes you have not read the source and will not.

`CLAUDE.md` in the repository root explains *why* the code is shaped the way it is; it is written for
somebody changing it. This file is the other half: what to configure, what will destroy data if you get
it wrong, and what this repository deliberately does **not** provide.

---

## 0. Read this part before anything else

Three things here can cause **unrecoverable** loss. None of them announces itself.

### The encryption key ring is not recoverable from a database backup

Candidate names, contact details, experience summaries, education fields and readability advice are
sealed with AES-GCM under keys that live in `Encryption:Keys:*` — **environment variables, not the
database**. A backup of SQL Server contains the ciphertext and none of the keys.

**Lose the key ring and every encrypted column is permanently unreadable.** Not degraded: the resume
stops loading at all, because those columns are eagerly loaded owned properties.

So: back the key material up **separately from the database**, to somewhere that survives losing the
host — a secrets manager, or a sealed envelope in a safe. Test that you can read it back *before* the
first real candidate signs up. A backup you have never restored is a belief, not a backup.

The same is true of `Encryption:BlindIndex:Keys:*`, with a different failure: lose those and exact-match
lookups stop working, which means **nobody can log in** (the email lookup goes through the blind index)
even though every row is intact.

### There is no backup, and nothing in this repository makes one

`docker-compose.app.yml` keeps SQL Server's data in a named Docker volume. `docker compose down -v`
erases it. So does losing the host. There is no scheduled dump, no point-in-time recovery and no
retention policy anywhere in this repository, because backup policy is an operational decision.

**This is the strongest reason to use a managed SQL instance rather than the bundled container** —
Azure SQL, RDS and Cloud SQL all bring automated backups and point-in-time restore that a volume does
not.

### The bundled SQL Server is not licensed for production

`MSSQL_PID` defaults to `Developer`, which is free and full-featured and which Microsoft licenses for
**development and test only**. Setting the variable to a production edition requires the licence to go
with it. The recommended answer is not to run that service at all — see §3.

---

## 1. What you are deploying

Three services, and only one of them should ever be reachable from the internet:

| Service | Port | Faces the world? |
|---|---|---|
| `web` (Next.js, the BFF) | 3000 | **Yes**, behind your TLS proxy |
| `api` (`BuildCv.Api`) | 8080 | **No.** Reachable only from `web` |
| `sqlserver` | 1433 | **No.** Ideally not this service at all |

The browser never holds a BuildCv credential: it talks to `web`, and `web` calls the API server-side
with a bearer token. That is the whole security argument for this topology, and it is why
`Cors:AllowedOrigins` is empty and must stay empty.

**Do not publish the API.** If you ever do, CORS, `SameSite` and `Cross-Origin-Resource-Policy` all
become live concerns that this deployment currently gets to ignore, and the "clients must refresh
proactively, not on 401" contract in `docs/api-contract.md` starts applying to real browsers.

The compose file publishes `web` on `127.0.0.1:3000` — a laptop default. In production, leave it bound
to loopback (or a private interface) and put your TLS terminator in front of it. Do not change it to
`0.0.0.0:3000` and call it done: that serves the product over plaintext HTTP.

---

## 2. Secrets

Every placeholder in `.env.example` is printed in a committed file, so **anything left as-is is a key an
attacker already has**. The application refuses to start rather than inventing defaults, which is the
behaviour to keep.

```bash
openssl rand -base64 32     # Encryption:Keys:v1:Aes        — run once
openssl rand -base64 32     # Encryption:BlindIndex:Keys:b1 — run again, a DIFFERENT value
openssl rand -base64 48     # Jwt:SigningKey — any string of 32+ characters
```

| Variable | What it is | If it leaks | If you lose it |
|---|---|---|---|
| `BUILDCV_ENCRYPTION_KEY` | AES-GCM key ring | Every CV is readable | **Every CV is unreadable, forever** |
| `BUILDCV_BLIND_INDEX_KEY` | HMAC key for lookups | Email addresses become guessable by dictionary | **Nobody can log in** |
| `BUILDCV_JWT_SIGNING_KEY` | Token signature | Anyone can mint a session as anyone | Everyone is logged out once |
| `MSSQL_SA_PASSWORD` | Database login | Full database access | Rotate it |

`docker compose -f docker-compose.app.yml config` resolves every `${VAR:?}` and is the cheapest check
that nothing is missing. Run it before deploying, not after.

**Rotating an encryption key means adding one, never replacing one.** Reads use every configured key and
writes use only the active one, so:

```yaml
Encryption__Keys__v1__Aes: <the old key, still needed to read old rows>
Encryption__Keys__v2__Aes: <the new key>
Encryption__ActiveKeyId: v2
```

Repointing `v1` at new material makes every row already written under it unreadable. The key **ids** are
literals in `docker-compose.app.yml` and have to be — Compose does not substitute variables in a mapping
key — so rotating means editing that file.

---

## 3. The database

**Use a managed instance.** Point `ConnectionStrings__BuildCv` at it and do not run the `sqlserver`
service at all. You get backups, patching, point-in-time restore and a supported licence, none of which
the container has.

If you run the container anyway, you own all four of those, and §0 applies with full force.

### Applying the schema

The API **will not** migrate itself. `Program.cs` gates auto-migration on `IsDevelopment()` and the
container runs Production — deliberately, because the process serving traffic should not own the schema
and would re-run the migration once per instance.

The `migrator` service does it: a one-shot container that applies an idempotent script generated at image
build time and exits, with `api` waiting on `service_completed_successfully`. Re-running is a no-op, so
it is safe on every deploy.

Against a managed instance, point it at the server:

```yaml
migrator:
  environment:
    MIGRATION_SERVER: your-instance.database.windows.net
    MIGRATION_USER: buildcv_migrator
    MIGRATION_PASSWORD: ${BUILDCV_MIGRATION_PASSWORD:?}
    # Most managed instances forbid CREATE DATABASE from an application login and provision the
    # database themselves.
    MIGRATION_CREATE_DATABASE: "false"
```

The generated script is a reviewable artifact: `docker compose -f docker-compose.app.yml build migrator`
then read `/migrations/BuildCv.sql` out of the image if you want to see what will run before it runs.

### Two migrations destroy data, and one of them is a one-way door

- `20260802051841_EncryptLanguageFluency` **drops every stored `Fluency` value**, in both directions.
  That is display-only free text no scoring path reads, and candidates can retype it — but announce it
  before deploying, because it is silent.
- `20260801140223_AddSectionScoringAndRecommendations` is **forward-only in practice**. Its `Down()` is
  faithful and still destroys `scoring.Recommendations` in full plus every `Analyses.LanguagesScore`.
  Plan the deploy knowing there is no rolling back past the first write.

There is also no rollback past `feat/date-precision`: a month-precision date written after it ships
cannot be parsed by an older build, and the field is eagerly loaded — so it is a resume that no longer
loads, not a lost field.

---

## 4. Behind a proxy: the setting that will otherwise take you down

Rate limiting partitions on the peer address. **Behind any reverse proxy, ingress or CDN, every client
collapses into the proxy's single partition**, and the 5-request-per-minute auth window becomes a global
5/min cap for the entire deployment. That is a self-inflicted denial of service that also throttles no
individual attacker.

It is **off by default and that default is correct for direct exposure**: `X-Forwarded-For` is
client-controlled, so an unrestricted `UseForwardedHeaders` lets any caller claim a fresh source address
per request and defeat rate limiting entirely — worse than the collapsed partition.

So enabling it requires naming the proxies. The app throws at startup if the allowlist is empty.

```json
"Network": {
  "ForwardedHeaders": {
    "Enabled": true,
    "KnownProxies": ["10.20.30.5"],
    "KnownNetworks": [],
    "ForwardLimit": 1
  }
}
```

- Prefer `KnownProxies` alone whenever the proxy address is stable. It is the narrowest thing you can
  write.
- Reach for `KnownNetworks` only for an autoscaling proxy tier, and **size it to that tier, not to your
  site**: on a flat internal network `10.0.0.0/8` trusts every internal host to set its own
  `X-Forwarded-For`, which is precisely the failure this setting exists to prevent.
- Keep `ForwardLimit` equal to the real hop count between the client and Kestrel.

Only `X-Forwarded-For` and `X-Forwarded-Proto` are honoured. `X-Forwarded-Host` never is.

**Verify it after deploying**, because a wrong value fails silently until traffic arrives: make six
login attempts from one machine and confirm the sixth is throttled, then make one from a second machine
and confirm it is *not*. If the second is also throttled, the partition has collapsed.

---

## 5. Health probes

| Probe | Use it for | Never use it for |
|---|---|---|
| `GET /health/live` | Container restarts | Load-balancer readiness |
| `GET /health/ready` | Load-balancer readiness | Container restarts |

Liveness touches nothing outside the process, on purpose. A failed liveness probe *restarts* the
container, so pointing it at the database would roll-restart the whole fleet the moment the database
hiccuped — at the moment it can least afford a reconnection stampede. The `Dockerfile`'s `HEALTHCHECK`
already probes liveness for exactly this reason; do not "improve" it.

Readiness opens a database connection and answers 503 when it cannot. Both are anonymous, both are
exempt from rate limiting, both are plain text, and both live **outside `/v1`** so they do not move when
the product contract versions.

---

## 6. Logs

`ASPNETCORE_ENVIRONMENT=Production` selects `appsettings.Production.json`, which turns on the access log
and switches the console formatter to JSON so a log aggregator can query fields instead of parsing
sentences. Every request carries `X-Correlation-ID`, echoed to the caller and attached to every line the
request writes — it is what turns "a user reports an error" into "here is the request".

**Nothing about a CV reaches a log line, a metric tag or a span**, and that is enforced by a test rather
than by convention. Keep it that way: this repository treats a log line as covered by none of its
encryption, because it is shipped to an aggregator with its own retention and access list. A leaked row
can be re-encrypted; a leaked log line has already been indexed and replicated.

There is **no metrics or tracing exporter**. The instruments exist — meters `BuildCv` and
`BuildCv.Infrastructure.Encryption`, and the `BuildCv` activity source — and nothing collects them.
Wiring an OpenTelemetry exporter is a deliberate follow-up, not an oversight; until then `StartActivity`
returns null and every span costs one null check.

---

## 7. What is not built

Named so you plan around them rather than discover them:

- **No mail provider.** `UnconfiguredEmailSender` refuses and `POST /v1/auth/password-reset` answers
  **503**. Password recovery does not work until you register a real `IEmailSender` in
  `AddInfrastructure` — one line, once you have picked a provider and a sending domain with its SPF and
  DKIM records. Email verification is unbuilt for the same reason.
- **No background jobs, and no caching layer.** Both were deliberate; the `Analyses` table *is* the
  scoring cache, through de-duplication.
- **No rate limiting on the web tier.** The API's limiters protect the API. Your proxy is the only thing
  in front of Next.js.
- **No admin surface.** `Admin` can read any resume and any posting; there is no console.

---

## 8. Before you announce it

- [ ] Every placeholder in `.env` replaced, and `docker compose ... config` resolves cleanly
- [ ] Encryption and blind-index keys backed up **outside** the database, and a restore rehearsed
- [ ] Database backups running, and a restore rehearsed against a real dump
- [ ] SQL Server licensed, or a managed instance in use
- [ ] `Network:ForwardedHeaders` configured, and the two-machine throttle test above passed
- [ ] TLS terminating in front of `web`; API and database publish no ports
- [ ] Readiness wired to the load balancer, liveness to the container runtime
- [ ] Log aggregation collecting JSON, and correlation ids queryable
- [ ] The `Fluency` data loss announced, if you are upgrading rather than installing fresh
- [ ] A decision recorded about password recovery being unavailable until a mailer exists
