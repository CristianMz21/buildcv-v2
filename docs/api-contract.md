# BuildCv API — the client contract

For whoever is building against this API. It assumes you have not read the backend and never will.

**This is not a substitute for the OpenAPI document.** Every route, every request field, every response
field and every declared status code is published at `/openapi/v1.json` (Development only — see
*Running it locally*), and that document is generated from the code, so it cannot drift. Read it for
*shapes*. Read this for the things a schema cannot express: the order calls must happen in, which
refusals are not JSON, what each ceiling is, and which security controls look like bugs and are not.

Every path below carries the `/v1` prefix. It is a real URL segment, not a header or a media type;
`/health/live` and `/health/ready` sit outside it on purpose, because probe URLs live in deployment
manifests and must not move when the product contract versions.

---

## 1. You are probably building a BFF, and that makes most of this easy

The intended client is a **backend-for-frontend**: the browser calls your own server (Next.js route
handlers, or equivalent), and your server calls this API with an `Authorization: Bearer` header.

If that is your shape, three things are true and you should stop worrying about them:

- **CORS does not apply.** It is a browser rule. Your server is not a browser. This API ships with
  `Cors:AllowedOrigins` empty, which means the CORS middleware is *not added to the pipeline at all* —
  there is nothing to configure and nothing to relax.
- **`SameSite=Strict` on the auth cookies does not apply.** Also a browser rule. If you send a bearer
  header, you never touch the cookies.
- **`Cross-Origin-Resource-Policy: same-origin` does not apply.** It governs whether a *browser* will
  let a page embed this response as a subresource. A server-to-server `fetch` is subject to none of it.

**Do not "fix" any of the three.** They are load-bearing for the browser-facing case, they cost the BFF
case nothing, and each one is a control somebody would have to argue back into place. If a call from
your server is failing, it is not CORS — read the status code.

The rest of the security headers (`Content-Security-Policy: default-src 'none'`, `X-Frame-Options:
DENY`, `Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff`, `Permissions-Policy`,
`Cross-Origin-Opener-Policy`) are on every response for the same reason and are equally irrelevant to a
server-side caller.

There is a second supported shape — a browser talking to this API directly with cookies — and it is
where the CSRF and antiforgery machinery in §3 comes from. If you are building a BFF you can read §3.2
and skip §3.3 entirely.

---

## 2. Running it locally

```bash
cp .env.example .env          # sets MSSQL_SA_PASSWORD; .env is gitignored
docker compose up -d          # SQL Server 2022 on 127.0.0.1:1433
dotnet run --project src/BuildCv.Api
```

- **The database schema applies itself.** In Development only, and only with `Persistence:Provider` set
  to `SqlServer` and `Persistence:AutoMigrate` true (both are the committed defaults), the app runs EF
  migrations at startup. No `dotnet ef database update` step.
- **No connection string to configure.** With none set, Development falls back to a committed default
  that already matches `docker-compose.yml`. To point somewhere else, set
  `ConnectionStrings__BuildCv`.
- **Ports**: `http://localhost:5062` and `https://localhost:7160`.
- **OpenAPI**: `GET /openapi/v1.json`, anonymous, **Development only**. There is no Swagger UI; point
  your generator or an editor at the JSON.
- **Auth cookies are not `Secure` in Development**, so plain-http debugging works. Every other
  environment sets `Secure` unconditionally.
- **Health**: `GET /health/live` (touches nothing) and `GET /health/ready` (probes the database). Both
  anonymous, both exempt from rate limiting, both plain text — `Healthy` / `Degraded` / `Unhealthy`, not
  JSON.

Every response carries `X-Correlation-ID`. Send your own to have it adopted (1–64 characters of ASCII
letters, digits and hyphen; anything else is replaced with a generated one rather than sanitised). Log
it — it is what makes a server-side error report actionable.

---

## 3. Authentication

Two credentials. A **JWT access token**, short-lived (15 minutes by default), and an **opaque refresh
token**, long-lived (30 days). Both are returned as cookies by `/v1/auth/login`; the access token is
*also* returned in the response body, which is what a BFF uses.

Roles are `Candidate`, `Recruiter` and `Admin`. **Self-registration can only grant `Candidate` or
`Recruiter`** — asking for `Admin` is a 400. Where a route requires a role, `Recruiter` and `Admin` both
satisfy a Candidate requirement, and `Admin` satisfies a Recruiter requirement.

**`Admin` is not a blanket override, and the gaps are deliberate.** Two reads widen for an Admin and
two pointedly do not:

| Route | Admin sees another account's data? |
|---|---|
| `GET /v1/resumes/{id}` | **Yes** — any resume |
| `GET /v1/jobs/{id}` | **Yes** — any posting, at any status |
| `GET /v1/scoring/{analysisId}` | **No.** Owner only |
| `GET /v1/readability/{reportId}` | **No.** Owner only |
| `POST /v1/scoring/score` | **No.** The resume must be the caller's |

The two refusals are a decision, not an oversight: a score and a readability report quote the
candidate's own CV text back at them, so they are not widened by reflex. If you are building an admin
view, plan for it to be able to open a resume and *not* its score history.

### 3.1 The sequence

```
POST /v1/auth/register     → 201, the account. DOES NOT LOG YOU IN.
POST /v1/auth/login        → 200 { accessToken, expiresIn }  + both cookies
   ... schedule a refresh off expiresIn ...
POST /v1/auth/refresh      → 200 { accessToken, expiresIn }  + both cookies, rotated
POST /v1/auth/logout       → 204, cookies cleared AND every refresh token revoked
```

Notes that cost people an afternoon:

- **`POST /v1/auth/register` does not authenticate you.** No cookie is set and no token is returned. Its
  `Location` header points at `/v1/auth/me`, and following it immediately answers **401** — correctly.
  Call `/v1/auth/login` next.
- **`expiresIn` is seconds**, and it describes the JWT, not the cookie. Default 900.
- **`POST /v1/auth/refresh` reads the refresh token from a cookie only.** There is no body and no header
  form. A BFF that wants long-lived sessions therefore has to hold and forward that cookie itself; a BFF
  that re-logs-in instead is also fine, at the cost of the 5/min auth window (§5).
- **Logout is log-out-everywhere.** `POST /v1/auth/logout` revokes *every* refresh token on the account,
  not just the caller's — the refresh cookie is path-scoped to `/v1/auth/refresh`, so no other endpoint
  can tell which token belongs to whom. It is `AllowAnonymous` (a caller with no usable credential must
  still be able to drop its cookies) and it accepts an **expired** access token, because the common
  logout is an idle tab. Access tokens already issued stay valid until they expire; revocation bounds
  the window to one access-token lifetime rather than closing it instantly.
- **A failed logout answers 500 and leaves the cookies in place.** That is deliberate: clearing them
  would remove the credential needed to retry while the session is genuinely still live server-side.
  Retry it.
- **`POST /v1/auth/change-password` also revokes every session** and clears the caller's cookies. Treat
  a successful password change as a logout: re-login before the next call.

### 3.2 If you are a BFF (bearer)

Send `Authorization: Bearer <accessToken>` on every call. That is the whole contract.

- **Bearer requests are exempt from the CSRF guard, by design.** It looks like a hole and is not: CSRF
  exists because a browser attaches cookies to cross-site requests *automatically*. A bearer header is
  never attached automatically by anything — an attacker's page cannot make your server send it. There
  is no ambient credential, so there is nothing to forge. This is exactly what makes the BFF pattern
  simple here, and it is why you can ignore §3.3.
  - One trap: "bearer request" means a **non-blank** `Authorization` value. Sending an empty
    `Authorization:` header while also holding an auth cookie does not disarm the guard — you will get
    the CSRF 403.
- You never need `GET /v1/auth/antiforgery`.
- Refresh on your own schedule off `expiresIn`. With no cookie in play, an expired bearer token answers
  a clean **401**, and a reactive retry loop is safe.

### 3.3 If you are a browser talking to this API directly (cookies)

1. `POST /v1/auth/login`. The cookies are set for you.
2. **Then** `GET /v1/auth/antiforgery` → `{ requestToken }`. Not before — see below.
3. Send that value as an `X-XSRF-TOKEN` header on **every** `POST`, `PUT`, `DELETE` and `PATCH`.
   Safe methods need nothing. Exempt paths need nothing: `/v1/auth/login`, `/v1/auth/register`,
   `/v1/auth/refresh` and `/v1/auth/antiforgery` itself. Everything else, including `/v1/auth/logout`,
   is guarded.
4. **Re-fetch the antiforgery token whenever the principal changes**: login, logout, account switch —
   **and access-token expiry**.

The token is bound to the principal it was issued for, and the binding is rejected with **403** in both
directions: a token obtained while anonymous fails once you hold a valid auth cookie, and a token
obtained while authenticated fails once your access token has expired and you read as anonymous again.

### 3.4 The one that will bite you: refresh proactively, never on 401

**Schedule your refresh off `expiresIn`. Do not wait for a 401 — for a cookie client it will not come.**

The access-token *cookie* deliberately outlives the *JWT* inside it (both cookies expire with the
refresh token, 30 days). That is what makes logout able to revoke: the browser used to delete the only
credential naming the account at the exact instant the token went stale, so an idle user pressing
"log out" arrived anonymous and nothing was revoked.

The consequence is a status-code flip. The CSRF guard triggers on **cookie presence**, so an idle
client's next unsafe request to any non-exempt route enters antiforgery validation as an *anonymous*
principal holding an *authenticated-bound* token — and answers:

```
403  {"title":"Forbidden","detail":"CSRF validation failed.", ...}
```

where a client with no cookie at all would have got **401**. **This is repo-wide, not specific to any
route**, and it breaks the reactive "on 401, refresh, retry" loop outright: the loop never fires,
because the 401 never arrives.

A session cookie would not avoid it either — an idle but still-open browser reaches the same state — so
it is inherent to the cookie outliving the JWT, which is what logout requires.

So: refresh off `expiresIn`, then re-fetch the antiforgery token. **Bearer clients are unaffected** and
see an ordinary 401.

---

## 4. Errors

Every error response is [RFC 7807](https://www.rfc-editor.org/rfc/rfc7807) ProblemDetails, typed
`application/problem+json` — **with exactly two exceptions, both listed below.** Dispatch on the content
type, not on the status code; that is what ProblemDetails exists for.

```json
{ "title": "Not Found", "status": 404, "detail": "Resume not found." }
```

| Status | When |
|---|---|
| 400 | Malformed body, a field that fails a domain rule, an unusable id, a cursor that will not decode |
| 401 | No usable credential |
| 403 | The thing exists and is not yours — **or** CSRF validation failed (read `detail` to tell them apart) |
| 404 | No such thing, or it is soft-deleted |
| 409 | A concurrent write got there first, or a unique value is taken. Reload and retry |
| 413 | Body over the endpoint's ceiling — **see below, this one is not JSON** |
| 429 | Rate limited. `Retry-After` is always present, in seconds |
| 500 | A real server fault. `detail` is generic outside Development |

Field-level errors — currently only `POST /v1/resumes/import` — add an `errors` object keyed by JSON
path, the same shape ASP.NET model validation emits:

```json
{ "title": "One or more validation errors occurred.", "status": 400,
  "errors": { "experience[2].endDate": ["Not a valid date."] } }
```

### 4.1 The two refusals that are not JSON

**A client must not assume every error has a parseable body.** Both of these are platform behaviour, both
were measured rather than assumed, and neither can be shaped from inside this application:

1. **413 Request Entity Too Large.** `Content-Length: 0`, no content type, `Connection: close`. The
   server enforces the size limit and tears the connection down before any application code runs.
   *Guard against it client-side*: check the body size against §5 before sending.
2. **A malformed (unterminated) multipart body on `POST /v1/resumes/import/extract`.** A bare 400 with
   an empty body. The framework's file-upload binding swallows the underlying error and answers
   directly. A *well-formed* multipart that simply omits the `file` part is different and comes back
   properly shaped.

Everything else, including the 429, has a body. If your parser sees an empty body on any other status,
that is a bug worth reporting.

---

## 5. Ceilings

Exceeding a rate limit is a 429 with `Retry-After` in seconds. All windows are fixed and one minute
wide.

### Rate limits

| Scope | Limit | Applies to |
|---|---|---|
| Per client address | **100 / min** | Everything except `/health/*` |
| Per client address | **5 / min** | `POST /v1/auth/register`, `/login`, `/refresh` — one shared window |
| Per client address | **20 / min** | `POST /v1/auth/logout` |
| **Per account** | **5 / min** | `POST /v1/auth/change-password` |
| **Per account** | **5 / min** | `POST /v1/resumes/import` |
| **Per account** | **10 / min** | `POST /v1/resumes/import/extract`, `POST /v1/resumes/import/propose` — one shared window |

Two things worth planning around:

- **The auth window is shared across register, login and refresh.** A test that registers and logs in
  has already spent 2 of its 5. If you refresh aggressively you will throttle your own logins.
- **"Per client address" means the address this API sees.** Behind a proxy that is the proxy, and
  everyone collapses into one bucket unless the deployment configures forwarded headers. IPv6 is charged
  per **/64**, not per address, so everything behind one delegation shares a window — deliberate parity
  with how IPv4 NAT already behaves. If your BFF calls this API from a small pool of egress addresses,
  size your traffic against the **100/min per address** ceiling, not per end user.

### Body size limits

| Route | Ceiling |
|---|---|
| `POST /v1/resumes/import` | 2 MiB |
| `POST /v1/resumes/import/extract` | 5 MiB |
| `POST /v1/resumes/import/propose` | 5 MiB |
| `POST /v1/job-offers/import` | 256 KiB |
| everything else | no endpoint-specific limit |

Over the ceiling is the unparseable 413 from §4.1, chunked bodies included. Check before you send.

---

## 6. Paginated lists

Every list is keyset-paginated. **There are no unbounded lists**, and there will not be.

```
GET /v1/resumes?limit=20
→ { "items": [ ... ], "nextCursor": "AAAAAAAAACo" }

GET /v1/resumes?limit=20&cursor=AAAAAAAAACo
→ { "items": [ ... ], "nextCursor": null }        ← last page
```

- `limit` is clamped into **1..100**, default **20**. Out-of-range values are clamped, not refused.
- `nextCursor` is **`null` on the last page**. That is the only end-of-list signal — do not infer it
  from a short page.
- **The cursor is opaque.** Its encoding is the server's to change. Send back exactly what you were
  given.
- **A cursor that will not decode is a 400**, never a silent restart at page one. If it were the latter,
  a client walking a list would quietly loop forever.
- **Cursors are not scoped to a list.** A cursor minted on `/v1/resumes` is *accepted* on
  `/v1/job-offers` and yields a valid-but-meaningless page. Do not share them between lists.

**Direction is not uniform, and it is deliberate:**

| Kind of list | Direction | Routes |
|---|---|---|
| Append-only evaluation history | **oldest first** | `GET /v1/resumes/{id}/analyses`, `GET /v1/resumes/{id}/readability` |
| Inventory of what you own | **newest first** | `GET /v1/resumes`, `GET /v1/job-offers` |

A history is replayed forwards because the question it answers is "did acting on the advice move the
number". An inventory is a list of what you have, so the newest is what you want first.

---

## 7. Two scores, and never add them together

- **`POST /v1/scoring/score`** grades a resume **against one job posting**. Its `overallScore` is a
  match percentage and is meaningless without the posting.
- **`POST /v1/resumes/{id}/readability`** grades the resume **on its own**, with no posting involved.
  Its `readabilityScore` is a different measurement on a different weighting.

They are separate aggregates with separate schema versions. The server will never combine them, and
`overallScore + readabilityScore` is not a number that means anything. If your UI wants one figure,
that is your product decision to make and to label.

Two consequences a client has to handle:

- **`isStale`** on an analysis is computed per request. `true` means the resume changed since that score
  was taken — or that the score predates the columns that could tell. Re-post to `/v1/scoring/score` to
  clear it. A readability report has **no** staleness signal at all; it records nothing about the
  resume's state to compare against.
- **A section whose `breakdown.weights.<section>` is `0` expressed no weighted requirement.** The
  `score` printed beside it measures nothing — do not render it as a result. The remaining weights are
  renormalized to total 1.0, so a low `overallScore` with only three recommendations is a complete
  answer, not a truncated one. `weights.languages` is `0` on every analysis this build can produce.

**Scoring is narrower than reading a posting**, and it is the one place the two authorization rules
differ. `POST /v1/scoring/score` requires the resume to be yours *and* the posting to be either
`Published` or yours — nothing else gets in. `GET /v1/jobs/{id}` additionally admits any `Admin` and
members of the owning organization. So a posting your account can *read* is not necessarily one it can
*score against*, and both refusals are a flat `403 "Forbidden."` with nothing distinguishing which of
the two failed. **Closing a posting takes it out of scoring for everyone but its owner** while leaving
it readable.

`POST /v1/scoring/score` is also **de-duplicated**: scoring the same resume against the same posting
twice in one day, with neither edited, returns the *same* run — same `id`, same `scoredAt`, no new entry
in the history. A repeated request is neither a no-op nor an error; it is the same scoring event.

---

## 8. The resume import flow

Three routes, in order, and the middle one is the reason the flow exists:

```
POST /v1/resumes/import/extract   (multipart, field name "file")  → raw text
POST /v1/resumes/import/propose   (multipart, field name "file")  → a DRAFT + importEvidence
   ... the candidate corrects the draft in your UI ...
POST /v1/resumes/import           (JSON: the corrected draft + importEvidence) → 201, the resume
```

- **`/propose` writes nothing.** It returns a draft plus an opaque `importEvidence` token; post the token
  back inside the body of `/v1/resumes/import` exactly as received. It is bound to the account and
  expires in 2 hours. A hand-edited token is a field error at `importEvidence`.
- **`/v1/resumes/import` is all-or-nothing.** If any field fails, nothing is created and you get the
  full `errors` object in one response — every failure at once, not the first one.
- Both multipart routes take the file under the form field name **`file`**.

---

## 9. Route inventory

`Auth` = any authenticated account. `Candidate` = any account (the role check admits Candidate,
Recruiter and Admin). `Recruiter` = Recruiter or Admin. `—` = anonymous.

| Route | Auth | Notes |
|---|---|---|
| `POST /v1/auth/register` | — | 201 + `Location: /v1/auth/me`. Does not log you in |
| `POST /v1/auth/login` | — | 200 `{ accessToken, expiresIn }` + cookies |
| `POST /v1/auth/refresh` | — | Reads the refresh **cookie**; no body form |
| `POST /v1/auth/logout` | — | 204. Revokes every session. CSRF-guarded |
| `POST /v1/auth/change-password` | Auth | Revokes every session; re-login after |
| `GET /v1/auth/me` | Auth | The caller's account |
| `GET /v1/auth/antiforgery` | — | Fetch **after** login; re-fetch on principal change |
| `POST /v1/resumes` | Candidate | Create an empty resume |
| `GET /v1/resumes` | Candidate | Paged summaries, newest first |
| `GET /v1/resumes/{id}` | Candidate | The full resume |
| `DELETE /v1/resumes/{id}` | Candidate | 204. Hides every analysis and report derived from it |
| `POST /v1/resumes/import/extract` | Candidate | multipart `file`, 5 MiB |
| `POST /v1/resumes/import/propose` | Candidate | multipart `file`, 5 MiB. Writes nothing |
| `POST /v1/resumes/import` | Candidate | 2 MiB. All-or-nothing, field-keyed errors |
| `PUT /v1/resumes/{id}/contact` | Candidate | |
| `POST /v1/resumes/{id}/{section}` | Candidate | `skills`, `experiences`, `educations`, `certificates`, `projects`, `languages`, `awards`, `publications`, `interests`, `references` |
| `DELETE /v1/resumes/{id}/{section}/{itemId}` | Candidate | `itemId` is an **int**, not a guid |
| `POST /v1/resumes/{id}/readability` | Candidate | Evaluates the CV as it stands now. Always writes a new run |
| `GET /v1/resumes/{id}/readability` | Candidate | Paged history, **oldest first** |
| `GET /v1/readability/{reportId}` | Auth | One stored report |
| `GET /v1/resumes/{id}/analyses` | Candidate | Paged score history, **oldest first** |
| `POST /v1/scoring/score` | Auth | De-duplicated; see §7 |
| `GET /v1/scoring/{analysisId}` | Auth | One stored analysis |
| `POST /v1/jobs` | **Recruiter** | Create a posting |
| `GET /v1/jobs/{id}` | Auth | Widest read here: **any authenticated account** if the posting is `Published`; plus the owner, **any `Admin`**, and members of the owning organization at any status. Scoring is narrower — see §7 |
| `POST /v1/jobs/{id}/publish` | Auth | |
| `POST /v1/jobs/{id}/close` | Auth | |
| `POST /v1/job-offers/import` | Candidate | 256 KiB. A candidate's own copy of an offer |
| `POST /v1/job-offers/extract` | Candidate | Pulls requirements out of pasted text |
| `GET /v1/job-offers` | Candidate | Every posting **you own**, newest first — including ones created at `POST /v1/jobs`. Not postings owned by an organization you belong to |
| `POST /v1/organizations` | Auth | |
| `GET /v1/organizations/{id}` | Auth | |
| `GET /v1/organizations/slug/{slug}` | Auth | |
| `POST /v1/organizations/{id}/members` | Auth | |
| `DELETE /v1/organizations/{id}/members/{accountId}` | Auth | |
| `GET /health/live` `GET /health/ready` | — | Outside `/v1`. Plain text. No rate limit |

---

## 10. Small things that are decisions, not accidents

- **Every enum on the wire is a NAME, never a number.** `"role": "Candidate"`, `"band": "Good"`,
  `"level": "Native"`. The numbers behind them are a storage detail and may be renumbered; the names may
  not. Type these as string unions.
- **Every id on the wire is a bare GUID string**, never `{"value": "..."}` — except a resume **item**
  id, which is an `int`.
- **A resume item id is opaque, unique within one resume, and promised nothing else.** Not dense, not
  ordered, not stable across a delete-and-re-add, and *not* unique across resumes. It is only valid
  paired with the resume you read it from.
- **A GUID of all zeros is refused with 400 on every route that takes one**, in the path or in a body.
  It is a syntactically valid GUID that can never name a row.
- **Inputs are more tolerant than outputs.** A level field accepts the name in any casing *and* the
  legacy numeric form; it always comes back as the name. Do not round-trip the number — send the name.
- **Dates on a resume carry the precision the candidate stated**: `2015`, `2015-06` or `2015-06-30`, as
  strings, never a full timestamp. Render what you were given; a month-precision date rendered as "1
  June" is a claim the candidate did not make. **The input side is narrower than the output side**: the
  per-section routes (`POST /v1/resumes/{id}/experiences` and friends) accept a full `yyyy-MM-dd` only,
  so a candidate can *import* "June 2015" and cannot *type* it. Known asymmetry; plan your date picker
  around the full form.
- **Deletes are soft, and there are only two of them.** `DELETE /v1/resumes/{id}` and
  `DELETE /v1/resumes/{id}/{section}/{itemId}` (plus removing an organization *member*). A deleted
  resume answers 404 afterwards and takes every analysis and readability report derived from it with it.
  Nothing else in this API is deletable — see §11.
- **`GET /v1/readability/{reportId}` and `GET /v1/scoring/{analysisId}` answer 404 for an id that never
  existed and 403 for one owned by someone else.** Do not surface the difference to end users.
- Nothing about a CV ever reaches a log, a metric or a trace attribute. If you build server-side logging
  around this API, hold the same line.

---

## 11. What does not exist yet

Listed because you will otherwise spend an afternoon looking for it in the OpenAPI document. None of
these is a bug report; they are gaps in the surface, and the workaround is given where there is one.

- **There is no way to EDIT a resume item.** Sections have `POST` (add) and `DELETE` (remove) and
  nothing else — the only `PUT` in the whole API is `/v1/resumes/{id}/contact`. Editing one bullet point
  means delete-then-re-add. The `itemId` is explicitly **not** promised to survive that, so re-read the
  resume afterwards rather than assuming the id you had is still the entry you meant.
- **Nothing is deletable except a resume, a resume's items, and an organization membership.** A job
  posting, an imported job offer, an account and an organization are permanent once created. There is no
  account-closure route.
- **A posting cannot be archived through the API**, only published (`POST /v1/jobs/{id}/publish`) and
  closed (`POST /v1/jobs/{id}/close`). `Archived` appears in the `status` enum and no route can produce
  it, so handle it defensively if you switch on the value and otherwise expect never to see it.
- **No route lists an organization's postings.** `GET /v1/job-offers` returns what *your account* owns,
  by account, never by organization. A recruiter dashboard has to hold the ids itself and fetch
  `GET /v1/jobs/{id}` one at a time.
- **No route lists the organizations you belong to**, and there is no "leave organization" for yourself
  — you reach an organization by id or by slug, both of which you must already know, and membership is
  removed by someone calling `DELETE /v1/organizations/{id}/members/{accountId}`.
- **No email verification flow reachable over HTTP.** `isEmailVerified` is on the account and is always
  `false`: the server has a handler for it, but **no route is mapped to it**, so nothing a client can
  call will ever flip it. Do not gate your UI on the field.
- **No password reset.** `POST /v1/auth/change-password` requires the current password, so a forgotten
  password is unrecoverable.
- **No language requirements on a posting**, which is why `weights.languages` is `0` on every analysis
  (§7).
