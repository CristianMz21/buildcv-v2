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

That is measured, not inferred. Starting the API against a resume written under a different key:

| | Result |
|---|---|
| API startup | **succeeds** — the key ring is validated for format, never against the data |
| `GET /v1/resumes` | **500** on every request |
| `GET /health/live` | **200** |
| `GET /health/ready` | **200** |

**Neither health probe can see this.** Readiness opens a database connection and the connection is fine;
nothing it does decrypts anything. So a key problem presents to your monitoring as *a perfectly healthy
service*, while every candidate gets an error on every page — and the only signal is the log line naming
`AesGcmFieldEncryptor` and a failed authentication tag.

#### On Azure they are recoverable, and knowing that changes what you actually guard

`deploy/azure.sh` prints the generated keys once and says to save them. It also **stores them as
Container Apps secrets**, so on this deployment they can be read back:

```bash
az containerapp secret show -g buildcv-rg -n buildcv-api --secret-name enc   --query value -o tsv
az containerapp secret show -g buildcv-rg -n buildcv-api --secret-name blind --query value -o tsv
```

Verified retrievable rather than assumed — checked by length and digest rather than by printing, since
that command's output is a production encryption key and a terminal is a log.

**This is a correction to the calibration, not a licence to stop keeping a copy.** What it changes is
*which* event is catastrophic. Closing the terminal is not; the real single points of failure are:

- **`az group delete`**, which destroys the secrets and the database in one command, and is the
  suggested teardown at the end of `deploy/azure.sh`.
- **Losing the subscription** — an expired card on a personal account reaches the same place.
- **Migrating off Azure**, where the keys must travel *separately* from the data and nothing carries
  them automatically.

So keep the offline copy for those three, not against forgetfulness. And note the asymmetry that makes
this worth stating at all: a database backup and a key backup taken together are one artifact holding
both halves, which is the thing the encryption exists to prevent. Store them apart.

Two consequences worth acting on:

- **Alert on the 5xx rate, not only on the health probes.** A deployment watching liveness and readiness
  alone would have found out about this from a user.
- **A wrong key is recoverable and a lost one is not.** Restoring the correct value brought every resume
  straight back in the test above — nothing on disk was harmed. The failure is total and reversible
  right up until the key is gone, which is exactly why §0 asks you to rehearse reading the backup rather
  than trusting that it exists.

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

### Rehearsing the restore, with the commands that work

Do this before the first real candidate signs up, not after. It has been executed end to end against
the bundled container; every step below is the one that worked, including the parts that fail silently
if you skip them.

```bash
COMPOSE="docker compose -f docker-compose.app.yml"
SQL="/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $MSSQL_SA_PASSWORD -C -b"

# 1. Back up. The directory does not exist and SQL Server cannot create it.
$COMPOSE exec -T --user root sqlserver sh -c \
  'mkdir -p /var/opt/mssql/backup && chown mssql:root /var/opt/mssql/backup'
$COMPOSE exec -T sqlserver $SQL -Q \
  "BACKUP DATABASE [BuildCv] TO DISK='/var/opt/mssql/backup/BuildCv.bak' \
   WITH FORMAT, INIT, COMPRESSION, CHECKSUM;"

# 2. Get it OFF the host. A backup inside the volume dies with the volume.
docker cp "$($COMPOSE ps -q sqlserver):/var/opt/mssql/backup/BuildCv.bak" ./BuildCv.bak

# 3. Restore. WITH MOVE is required -- the logical names inside the backup are
#    BuildCv and BuildCv_log, and they have to be remapped onto this server's paths.
docker cp ./BuildCv.bak "$($COMPOSE ps -q sqlserver):/var/opt/mssql/backup/BuildCv.bak"
$COMPOSE exec -T --user root sqlserver \
  chown mssql:root /var/opt/mssql/backup/BuildCv.bak
$COMPOSE exec -T sqlserver $SQL -Q \
  "RESTORE DATABASE [BuildCv] FROM DISK='/var/opt/mssql/backup/BuildCv.bak' \
   WITH MOVE 'BuildCv' TO '/var/opt/mssql/data/BuildCv.mdf', \
        MOVE 'BuildCv_log' TO '/var/opt/mssql/data/BuildCv_log.ldf', RECOVERY;"
```

Three things that cost time if nobody wrote them down:

- **`chown mssql` on the copied `.bak`.** `docker cp` writes it as root with the host's umask, and SQL
  Server runs as `mssql`. It reports "Operating system error 5(Access is denied)" — a permission, wearing
  the clothes of a missing file. Same trap as the migration script and the published app files.
- **`WITH MOVE` is not optional**, even restoring onto the same image. The logical names travel inside
  the backup; the paths do not.
- **`identity` is a reserved word.** A hand-written query against the accounts table needs
  `[identity].Accounts`, or SQL Server answers `Incorrect syntax near the keyword 'identity'`.

**Then start the stack normally.** `migrator` runs its idempotent script over the restored schema and
exits 0 — a restore does not need a different startup path.

### Taking them on a schedule

There is a `backup` sidecar, **off unless you ask for it**:

```bash
mkdir -p ./backups
# The directory must be writable by SQL Server, which runs as uid 10001. Via Docker rather than host
# sudo, because sudo is not available everywhere Docker is:
docker run --rm -v "$(pwd)/backups:/b" alpine chown 10001:10001 /b

docker compose -f docker-compose.app.yml --profile backup up -d
```

It backs up, **reads the file back with `RESTORE VERIFYONLY`**, prunes past the retention window, sleeps
and repeats. A backup nobody has read is a belief, and the only cheap moment to find out is when it is
written.

| Variable | Default | |
|---|---|---|
| `BACKUP_INTERVAL_HOURS` | 24 | |
| `BACKUP_RETENTION_DAYS` | 14 | |
| `BACKUP_DIRECTORY` | `./backups` | A **host** path, so it survives `docker compose down -v` |

**The numbers are not decided here.** How often and how long to keep are your policy; that a backup
happens at all, and that somebody checks it is readable, are not.

Four things about it that are not obvious:

- **It is behind a profile** so a laptop does not silently start writing dumps, and a production
  deployment does not get them by accident either.
- **The directory is mounted on `sqlserver`, not on the sidecar.** `BACKUP DATABASE ... TO DISK` writes
  on the **server**; sqlcmd only sends the statement. Mounted on the sidecar it produced *"Operating
  system error 5(Access is denied)"* from a path that container could see and the server could not — a
  backup service that never wrote a byte. Measured.
- **Do not run it against a managed instance.** Azure SQL, RDS and Cloud SQL all do this better, with
  point-in-time recovery this cannot offer.
- **Files land mode `640`, owned by uid 10001**, which means your host user cannot read them without
  going through a container. That is correct: a `.bak` is the entire database.

**It does not back up the key ring**, and it prints that on every start. Nothing automated here can: the
keys live in configuration, and a dump restored without them returns every row and no readable one.

### What the rehearsal proves, and it is the point of §0

Restoring the database is **half** of a recovery. Executed here with a marked CV written before the
backup:

| | Result |
|---|---|
| Login with an account that predates the backup | **succeeds** — the blind-index keys still resolve the address |
| The marked CV, read back through the API | **returns decrypted** |

Both halves needed the **same key ring**. A restore with the wrong keys gives you every row and no
readable one; the keys with no backup give you nothing at all. That is why §0 asks you to rehearse
reading the key material back, not merely to store it.

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

## 4b. What the heaviest endpoints actually cost

Measured at the **documented ceilings**, not at a plausible size — a CV filled to every limit the draft
validator allows (200 skills, 50 experiences with 50 bullet points each, 50 projects, ~2,900 items in
one request), a posting with the maximum 100 requirements, and a document at the 5 MiB upload limit.

| Endpoint | Input | Time |
|---|---|---|
| `POST /v1/resumes/import/propose` | 5 MiB document | **4.1 s** |
| `POST /v1/job-offers/import` | 100 requirements | 2.7 s |
| `POST /v1/scoring/score` | 200 skills × 100 requirements | 2.5 s |
| `POST /v1/resumes/import` | CV at every ceiling | 2.3 s |
| `POST /v1/resumes/import/extract` | 5 MiB document | 0.6 s |
| `POST /v1/resumes/{id}/readability` | CV at every ceiling | 0.5 s |
| `GET /v1/resumes/{id}` | CV at every ceiling | 0.4 s |

**Nothing approaches a client timeout**, which is what this table exists to establish: the BFF in front of
this API bounds every call at 20 seconds, and the slowest thing the API can be asked to do finishes in
about a fifth of that.

Read it as a **shape**, not as a capacity figure. One request at a time, no contention, SQL Server in a
container on the same host. What it rules out is an endpoint that is inherently slow — none of these is
doing twenty seconds of work — and what it does not tell you is how any of them behave under concurrent
load, which only your own traffic will.

`POST /v1/resumes/import/propose` is the one to watch: it is the only endpoint whose cost is driven by a
file somebody else chose, and it parses inside the request because there are no background jobs.

### The rate limiter and the real client address — measured twice, with different answers

This section said the limiter **could not** tell users apart. On the Azure deployment that is no longer
true, and the correction is here rather than as an edit because both measurements were honest and the
difference between them is the useful part.

**On Azure Container Apps it works.** Read out of the API itself with the diagnostic below, driving
`POST /v1/auth/login` through the public front door:

```
peer is now 104.28.166.241, was [::ffff:100.100.0.141]:40976 before trust ran;
unconsumed X-Forwarded-For is <absent>.
```

`104.28.166.241` is the real client's public address, confirmed against an external echo service in the
same minute. Three facts, all of them load-bearing:

- **`was …` is populated**, so `UseForwardedHeaders` ran and the peer was on the allowlist. That is the
  half that was silently failing before, and it is what makes the resolved address trustworthy at all.
- **The resolved peer is the client**, not the web container. `RateLimitPartitions.ClientKey` reads the
  same `Connection.RemoteIpAddress`, so two clients now land in two partitions.
- **`unconsumed` is `<absent>`**, so the chain was **no longer than** `ForwardLimit` and was consumed
  whole. That makes `2` demonstrably *sufficient* here. It does **not** make it exact: a one-entry chain
  would leave the header equally empty and resolve to the same address, and this diagnostic reports what
  the middleware *left* rather than what *arrived*, so it cannot tell those apart. What is established
  is that nothing was left over — which is the property that matters, because a leftover entry is where
  a caller's forged value would be sitting.

**A caller cannot pick its own partition, and that was tested rather than argued.** Three forged chains
— `9.9.9.9`, `8.8.8.8, 9.9.9.9`, and `1.1.1.1, 2.2.2.2, 3.3.3.3` — all resolved to the same real address,
and `unconsumed` stayed `<absent>` in every case. If a forged entry had arrived, the chain would have
been longer than two and something would have been left over. **Nothing was**, so a hop in front is
**replacing** `X-Forwarded-For` rather than appending to it.

That is worth stating plainly because it inverts the usual reading: an overwriting hop is normally the
thing that *defeats* forwarded headers, and here it is the thing that makes them safe — every entry the
API sees was written by infrastructure, not by a caller. **The safety therefore rests on the overwrite,
not on `ForwardLimit`.** A `ForwardLimit` raised past the real hop count is harmless only while that
holds; if a future front door starts appending, the same number begins reading attacker input. Re-run
the diagnostic whenever anything is added in front — that is the whole reason it exists.

**The earlier measurement was correct when it was taken, and is retained because the failure it
describes is the one you will meet first.** In the compose topology, with seven login attempts from
seven different addresses:

```
400 400 400 400 429 429 429
```

The fifth is throttled: five failed logins by anybody and nobody else can log in, register, refresh or
request a password reset for a minute. The cause was that **the BFF did not forward the header**, so
`Network:ForwardedHeaders:KnownProxies` naming the web container was inert — verified at the time by
sending `X-Forwarded-For: 203.0.113.77` through the proxy with `Enabled=true` and watching the API
record the web container's address anyway.

Two halves are needed, and the compose stack still only has one. Where the BFF does not forward, rate
limiting that distinguishes users has to live **in front of** the web container; Caddy can do it, as can
any ingress or CDN. **Do not carry either result across topologies** — this is exactly the setting whose
correct value is a fact about a deployment rather than about this code.

#### Adding a CDN in front silently breaks it, and `ForwardLimit` cannot fix that

Putting Cloudflare in front of the same deployment regressed it, in a way no error reports:

```
before the CDN   peer=104.28.166.241   ← the real client
after the CDN    peer=104.22.86.40     ← Cloudflare's edge
```

Both readings show the chain **consumed whole, with nothing left over**. It did not outgrow
`ForwardLimit`, so raising that number recovers nothing — there is no leftover entry to unwind to.

That is the overwrite showing its other face. **Azure Container Apps' external ingress replaces
`X-Forwarded-For` with the address it sees.** Before the CDN it saw the client, so the header carried
the client; afterwards it sees the CDN, so whatever the CDN appended is discarded before this API can
read it. The property that makes forged chains harmless is the same property that loses the client.

**The fix belongs at the BFF, not here.** Cloudflare's `CF-Connecting-IP` survives the rewrite because
the ingress rewrites only `X-Forwarded-For`, so the BFF reads that and forwards it. Confirmed by
re-reading the diagnostic afterwards — the peer returned to the real client address, matching an
external echo, on three consecutive probes.

**`True-Client-IP` is forgeable and must never be read.** Measured on this deployment: a client-supplied
`CF-Connecting-IP` is refused by Cloudflare with its own 403, while a client-supplied `True-Client-IP`
passed straight through to the origin. Two headers of the same convention, one protected and one not.
`ForwardedHeaderDiagnostics` reports all three so the question can be asked again after the next change,
and reads none of them — reporting a header is not believing it.

The operational rule: **re-run the diagnostic after anything is added in front of this deployment.**
Nothing fails, no probe turns red, and the only visible symptom is that a throttle everyone shares stops
being a throttle anyone notices.

#### The same lesson in the other direction: a CDN rewrites *responses* too

The request rewrite above has a mirror image, and it is easier to miss because the code looks correct
and the tests pass. Measured through the proxied hostname:

```
strict-transport-security: max-age=15552000
```

That is the **zone's** value, not any application's. Whatever an origin sends for a header the CDN
manages is replaced on the way out, so **a security header asserted in code is not a security header
delivered to a browser** once anything sits in front. Assert the ones you care about *through the edge*,
against the hostname a user actually types — a test that reads the origin's own response certifies a
value that never leaves the building.

**Not every header is rewritten, and which ones are is a per-edge fact.** Read off the same response:

| header | delivered to the browser |
|---|---|
| `strict-transport-security` | **replaced** by the zone's `max-age=15552000` |
| `content-security-policy` | passed through |
| `x-content-type-options`, `referrer-policy`, `x-frame-options`, `permissions-policy` | passed through |

So the rule is not "a CDN breaks your headers" but "a CDN owns the ones it manages". The only honest way
to know which is to fetch through the edge and read them.

**And the ones a browser receives are not this API's.** The CSP above is `default-src 'self'; script-src
'self' 'unsafe-inline'; …` — the web tier's. `SecurityHeadersMiddleware` sends `default-src 'none';
frame-ancestors 'none'`, which appears nowhere in that response, because the API is on **internal
ingress** and the only client that ever reads its headers is the BFF: server-side code that implements
no CSP, no HSTS and no framing policy.

That makes this API's whole security-header surface — `SecurityHeadersMiddleware` and `UseHsts()` alike
— **inert for browsers in the deployed topology**. Topology, not a defect, and worth keeping for the
same reason the cookie path is kept: a direct browser client would meet it. But do not tune any of it
expecting an effect on users, and do not read an assertion about it as an assertion about the product.
Nothing here configures HSTS either, so it carries the ASP.NET Core defaults (30 days, no
`includeSubDomains`) — which reach nobody.

#### How to check it yourself, instead of taking the paragraph above on trust

The measurement above is dated the moment anything in the chain changes — a proxy added, a CDN put in
front, the BFF starting to forward what it did not forward before. Enabling trust gives no signal either
way: the peer address simply becomes a different value, and nothing tells you whether it is the right
one. So the API can be asked directly.

```bash
# Container Apps
az containerapp update -g buildcv-rg -n buildcv-api \
  --set-env-vars Logging__LogLevel__BuildCv.Api.Security=Debug

# compose
BUILDCV_LOG_LEVEL=Debug docker compose -f docker-compose.app.yml up -d api
```

Make **one** request through the front door, then read the line:

```
Forwarded-header resolution: peer is now 203.0.113.7, was 100.100.0.103 before trust ran;
unconsumed X-Forwarded-For is <absent>.
```

Read it as three separate facts, because they fail independently:

| What you see | What it means |
|---|---|
| `was <absent> before trust ran` | trust is **off**, or the peer was not on the allowlist — the middleware never ran |
| peer is an internal address, `unconsumed` holds the real client | `ForwardLimit` is too **low**; raise it by the number of entries still sitting there |
| peer is an internal address, `unconsumed` is `<absent>` | nothing sent a chain — the hop in front is **overwriting** `X-Forwarded-For`, not appending, and no `ForwardLimit` fixes that |
| `<unsafe>` | the header held something that is not an address list, or arrived twice; see below |

**Turn it back off when you have the answer.** An address is personal data, and a log line carries none
of this repository's encryption — it is the one line here whose content comes from outside the process.
The level is the whole control: at the shipped `Information` the middleware writes nothing, which
`ForwardedHeaderDiagnosticsTests` pins in both directions rather than assuming.

`<unsafe>` is a refusal, not a bug. `X-Forwarded-For` is client input by definition, so a caller can put
a forged log line in it; values that are not an address list are **replaced whole** rather than trimmed,
the same ruling `CorrelationIdMiddleware` makes — a stripped value would report a chain nobody sent
while looking exactly like one they did. A repeated header is refused for a subtler reason: joining it
renders two headers of one hop identically to one header of two, and counting hops is the entire point
of the line.

### Releasing: `deploy/release.sh`

```bash
./deploy/release.sh                  # whatever main is at right now
./deploy/release.sh <40-char-sha>    # a specific build
```

**Releasing used to be one command for one of the two things that have to move together** — the
`az containerapp update ... --image` below — and that is exactly how the migrator ended up two
deployments behind the API. `verify.sh` catches that drift afterwards; this makes it impossible.

Five steps, and the order is the point:

1. **Both images are confirmed anonymously pullable** — before anything is touched, because a missing
   image otherwise surfaces as a revision stuck in `Activating`, or as a migration job that never starts
   after the old one has already been repointed.
2. Migrator repointed.
3. **The schema is applied and waited on.** Same order `docker-compose.app.yml` enforces with
   `service_completed_successfully`: a job runs to completion *before* the process serving traffic is
   replaced. Backwards, the new API serves against a schema it expects and does not have — surfacing as
   runtime errors on whichever request touches the new column first, not as anything the deploy reports.
   **A failed migration stops here and does not touch the API**, so the running deployment is unchanged.
4. API updated with `--image` (never `--yaml`), then waited on until the revision is `Running`. A
   revision that fails to start is not an outage — the previous one keeps serving.
5. **`verify.sh` runs**, and its exit code becomes the release's. A release is the update *plus*
   evidence.

It refuses a tag that is not a 40-character SHA, and refuses a SHA with no published image — both
before mutating anything, because every check is cheaper before the change than after it.

### Verifying the deployment: `deploy/verify.sh`

```bash
./deploy/verify.sh                                   # the live deployment
SITE=https://staging.example.com ./deploy/verify.sh  # somewhere else
SKIP_AZURE=1 ./deploy/verify.sh                      # HTTP checks only, no az login
```

Fifteen checks, and **not one of them reads a health status**. `healthState` is a small closed value
that a working app and an unprobed one produce identically, and a revision reports `Running` with no
probes declared at all — so every check either drives the product or reads a concrete configured value.
Each one corresponds to something that actually went wrong here: a dropped environment variable, a
registry credential outliving its registry, a `latest` tag moving under a running app, probes declared
the wrong way round, the diagnostic left at `Debug`, the origin hostname still answering after a CDN
went in front.

**It found real drift on its first run** — the migrator was two deployments behind the API, because the
API had been repointed twice and the job left behind. Harmless that day, since neither commit carried a
migration; the failure mode it prevents is an API expecting a table the migrator never created, which
surfaces as runtime errors rather than as anything a deploy reports.

Three exit codes, because there are three outcomes:

| exit | meaning |
|---|---|
| 0 | everything checked, everything passed |
| 1 | something is wrong |
| 2 | **inconclusive** — something could not be checked |

**The script throttles itself, and that is the rate limiter working.** The auth window is 5/min per
partition and the partition is now the client, so running this twice inside a minute earns a 429 from
your own deployment. That is reported as inconclusive rather than as a failure: a 429 here proves the
limiter partitions on you, and says nothing about whether the product works. Wait a minute.

`2` exists as a separate code because "nothing is known to be broken" is not "everything was checked",
and a pipeline that conflates them is how silent coverage loss goes unnoticed.

**It cleans up after itself.** The throwaway account is deleted at the end of the run, so nothing
accumulates in the product's own database — and deleting is the only honest way to check deletion, so
that costs a row and buys two checks: the `204`, and that the deleted account can no longer log in
(the `204` alone would look identical if the tombstone never landed).

Run it **on a machine that is not behind a VPN**, or with `dig` installed: the script resolves the
hostname through `1.1.1.1` and pins the address, because a local resolver on a WARP-style VPN can hand
back the *origin*, which then refuses with `403 RBAC: access denied` and reads exactly like an outage.

**`.github/workflows/verify-deployment.yml` runs the HTTP half daily**, and on demand via
`workflow_dispatch`. On a schedule rather than on deploy, because none of what it catches is caused by
a deploy — a certificate lapses, a DNS record is edited, an ingress restriction is widened, a CDN
setting changes, one of two image tags drifts. All of that happens *between* deploys. It carries **no
secrets**: the Azure-side checks need a credential, and a scheduled job holding one that can read the
production app is a standing risk taken to catch drift a human running the script locally catches
anyway. Run the full thing by hand after deploying.

### Deploying to Azure Container Apps

`deploy/azure.sh` maps this stack onto Container Apps. Five things it teaches that are not obvious, all
of them found by running it rather than reading the docs:

- **`az containerapp update --yaml` REPLACES the container definition; it does not merge.** A patch that
  declares `containers: [{name, image, probes}]` to add a probe silently drops every environment
  variable, and the app then fails `ValidateOnStart` on the missing `Jwt:SigningKey` — exit 139,
  crash-looping. Send the whole container block, or use `--set-env-vars`, which does merge. Measured;
  the previous revision stayed Active and Healthy, which is the only reason it was not an outage. The
  script now patches probes from the app's **own live template** read back with `az containerapp show`,
  because a block it never enumerated is a block it cannot drop.
- **Container Apps does not consult the image's `HEALTHCHECK`.** Probes must be declared in the app
  definition or there are none, and a container with no probe still reports Running. Declare
  `/health/live` as **Liveness** and `/health/ready` as **Readiness** — never the other way round: as
  liveness, `/health/ready` restarts every instance the moment the database goes away, into a database
  that is still down, undoing the recovery property §5 describes.
- **A region can refuse new SQL servers.** `eastus` and `eastus2` both answered
  `RegionDoesNotAllowProvisioning`, and a failed attempt still reserves the NAME, so the retry needs a
  fresh one as well as a different region. The retry can therefore land the database in a **different
  region from the apps**, which every query then pays a cross-region hop for; the script says so when
  it happens rather than leaving it to be discovered in a latency graph.
- **A successful `docker login` proves nothing about a push, and nothing about a pull either.** Login to
  `ghcr.io` succeeded with a token lacking `write:packages` and the push then failed; separately, a bare
  `curl` of a public manifest answers **401**, because GHCR wants a token it hands to anybody who asks.
  Both directions are false signals. `docker manifest inspect` performs the token exchange and is the
  only honest anonymous check — which is what the script's preflight uses, **before** creating a
  database, because a missing image otherwise surfaces as an app stuck in Activating twenty minutes later.
- **`az acr build` uses the CLASSIC Docker builder, not BuildKit** — kept here because the Dockerfile
  still carries its consequence. Any `COPY --chmod` fails there with `the --chmod option requires
  BuildKit` after building fine locally for as long as you like, so the Dockerfile uses `RUN chmod`,
  which works on both. This script no longer builds in Azure at all (see below), so nothing reaches that
  builder today; the Dockerfile keeps the portable form because the next person to try one will.

### Images come from GHCR, and no container registry is created

CI publishes `buildcv-api` and `buildcv-migrator` to `ghcr.io` on every push to `main`, tagged with the
full 40-character commit SHA and `latest`; the web client's repository publishes `buildcv-web` the same
way. The packages are **public**, so no app in this deployment carries a registry credential — the
`registries` array is empty on both apps and on the job, verified by running the migration job to
`Succeeded` after the old registry was deleted, rather than by reading a healthy status.

**An Azure Container Registry was the only line billing from day one** — about USD 5/month to hold three
images. It is deleted. What makes that safe is not that the images were copied out of it but that they
are **reproducible from CI**: a tag that is a commit SHA can be rebuilt from the commit.

Three things that cost real time to learn here:

- **A package created with a personal token is not linked to the repository**, so the workflow's
  `GITHUB_TOKEN` cannot write to it — `denied: permission_denied: write_package` on a workflow whose
  `permissions:` block is already correct. Fix it under the package's **Manage Actions access** by
  adding the repository with the **Write** role. Two separate settings get confused here: making the
  package **public** governs who can pull and does not grant the push, which is what was measured — the
  package was already public when the push was denied, and the same workflow re-run succeeded once the
  Actions-access grant was in place. `Add repository` offers **Read** first, which is the wrong one.
  Deleting the package also fixes it — CI recreates it linked and public — but only if nothing is
  running on it.
- **The GitHub API cannot change package visibility.** Two REST routes tested, both 404. It is a UI-only
  setting, so this cannot be scripted.
- **Public is the right default here and is not a security decision to re-take per change.** Both
  repositories are already public, so the images contain no secret the source does not; every secret
  this deployment holds is injected at runtime as a Container Apps secret. Private would mean a
  credential on every app and a token to rotate, for images whose source anyone can read.

**Prefer the SHA tag over `latest` when deploying.** `latest` moves under a running app: a replica that
restarts, or an app waking from zero, pulls whatever `latest` points at then — which nobody chose.

**The `min-replicas 0` trap, which applies to any registry migration.** A container app that scales to
zero **re-pulls its image when it wakes**. Deleting the old registry after repointing does not fail at
deletion — it fails silently, at whichever person opens the site first. A running revision proves
nothing, because it is not pulling. Force a scale-to-zero and a cold wake before deleting anything.

**`azurecontainerapps.io` is not in the Public Suffix List**, which `azurestaticapps.net` is — checked
against the published list. So every Container App shares one registrable domain and **any other
tenant's app is same-site with yours**. `SameSite=Lax` on a session cookie protects nothing against a
forged POST from a neighbour, and `Strict` does not either. It does not reach this API, whose ingress is
internal — but that is now a second reason for internal ingress, alongside the credential argument, and
it applies immediately to anything browser-facing on that domain.

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

**Readiness has to fail FAST, and two settings make it do so — neither is sufficient alone.** Measured
against a stopped SQL Server:

| Configuration | `/health/ready` answers |
|---|---|
| Neither | **nothing at all** in 60s — the probe hangs |
| Health-check `timeout` only | 503 in **22s** |
| Plus `Connect Timeout=5` | 503 in **~7s** |

The context uses `EnableRetryOnFailure`, so the check retries six times with backoff before answering,
and underneath that SqlClient's own `Connect Timeout` (default **15s**) dominates. Retrying is right for
a *request* — the user is waiting and a blip should not surface — and wrong for a *probe*, whose entire
job is to report the blip. A load balancer polling a hung endpoint gets a stuck socket instead of an
answer, on every instance at once.

`Connect Timeout` lives in the connection string, so **raise it if your database is not on the same
network** and accept the slower probe.

**A stopped database does NOT restart the API**, which is the liveness design working: with SQL Server
down, `/health/live` still answered 200 in 4 ms and the container was never recycled. When the database
came back the API recovered on its own — readiness returned in 0.25 s and writes succeeded — with **no
restart**. Do not "fix" this by pointing liveness at the database.

---

## 6. Logs

`ASPNETCORE_ENVIRONMENT=Production` selects `appsettings.Production.json`, which turns on the access log
and switches the console formatter to JSON so a log aggregator can query fields instead of parsing
sentences.

**The correlation id arrives as a queryable `CorrelationId` field**, not as prose in the message. That
takes `Logging:Console:FormatterOptions:IncludeScopes`, which is a different setting from the
`Logging:Console:IncludeScopes` in the base file — the latter is the legacy path the *simple* formatter
reads, and it stops applying the moment the formatter is `json`. Getting that wrong is silent: the
response header still carries the id and not one log line does. If you change the formatter, check that
a line still carries `"CorrelationId"` before trusting it. Every request carries `X-Correlation-ID`, echoed to the caller and attached to every line the
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

- **No mail provider is CONFIGURED, but none needs to be written.** Password recovery answers **503**
  until you set `Email:Smtp:Host`; setting it is the entire integration. See §7b. Email *verification*
  is still unbuilt — that is a feature, not a configuration.
- **No background jobs, and no caching layer.** Both were deliberate; the `Analyses` table *is* the
  scoring cache, through de-duplication.
- **No rate limiting on the web tier.** The API's limiters protect the API. Your proxy is the only thing
  in front of Next.js.
- **No admin surface.** `Admin` can read any resume and any posting; there is no console.

---

## 7b. Turning on password recovery

Four environment variables. There is no code change and no provider SDK: SES, Postmark, SendGrid,
Resend, Mailgun and a self-hosted Postfix all speak SMTP, so the choice is configuration.

```yaml
Email__Smtp__Host: smtp.eu.postmarkapp.com     # THE SWITCH. Empty = recovery answers 503
Email__Smtp__Port: "587"                        # submission + STARTTLS; 465 is implicit TLS
Email__Smtp__Username: ${BUILDCV_SMTP_USERNAME:?}
Email__Smtp__Password: ${BUILDCV_SMTP_PASSWORD:?}
Email__Smtp__FromAddress: no-reply@yourdomain   # SPF and DKIM are checked against THIS
Email__Smtp__FromName: BuildCv
```

- **The host is the only switch**, and there is deliberately no `Enabled` flag beside it. Two settings
  that can disagree about one fact is how a deployment ends up configured to send through a host it was
  told to ignore.
- **`FromAddress` is required once `Host` is set, and the app refuses to start without it.** Validated
  at startup rather than at send time because the send path *deliberately swallows its own failure* —
  reporting it would leak whether an address has an account — so a half-configured host would otherwise
  stay invisible until somebody noticed nobody was receiving mail.
- **SPF and DKIM are checked against `FromAddress`**, not against `FromName`. A provider account without
  those DNS records delivers to spam, which this API cannot detect and will report as success.
- **`AllowInvalidCertificate` is for a local Mailpit or MailHog and nothing else.** Accepting an
  unverified certificate on the connection that carries the SMTP password is the whole attack.

**Rehearse it before announcing.** The flow was verified end to end against a local SMTP server, and
what to check is what arrived, not what the API returned:

```bash
docker run -d --rm --name mailpit -p 127.0.0.1:1025:1025 -p 127.0.0.1:8025:8025 axllent/mailpit

Email__Smtp__Host=127.0.0.1 Email__Smtp__Port=1025 \
Email__Smtp__FromAddress=no-reply@buildcv.test \
dotnet run --project src/BuildCv.Api
```

Then `POST /v1/auth/password-reset`, open the message at `http://127.0.0.1:8025`, take the link out of
the body, and **use it**: confirm answers 204, the new password logs in, the old one does not, and a
second click on the same link answers 400 rather than 429. That last distinction matters — the auth rate
window is 5/min per IP, so a rushed rehearsal reports a throttle and reads like proof of single use.

## 8. Before you announce it

- [ ] Every placeholder in `.env` replaced, and `docker compose ... config` resolves cleanly
- [ ] Encryption and blind-index keys backed up **outside** the database, and a restore rehearsed
- [ ] Database backups running, and a restore rehearsed against a real dump — **including reading a CV back through the API afterwards**, which is what proves the key ring and the dump go together (§0)
- [ ] SQL Server licensed, or a managed instance in use
- [ ] Rate limiting that can tell users apart, **in front of** the web container — the API's own cannot
      in this topology, and the two-machine throttle test is what proves whichever you use (§4)
- [ ] TLS terminating in front of `web`; API and database publish no ports
- [ ] Readiness wired to the load balancer, liveness to the container runtime
- [ ] Log aggregation collecting JSON, and correlation ids queryable
- [ ] **An alert on the 5xx rate**, not only on the health probes — a key-ring problem answers 200 on
      both of them while every request fails (§0)
- [ ] The `Fluency` data loss announced, if you are upgrading rather than installing fresh
- [ ] `Email:Smtp:Host` set and a real reset link clicked through end to end (§7b) — or a decision
      recorded that password recovery is unavailable, which is what the 503 tells users
