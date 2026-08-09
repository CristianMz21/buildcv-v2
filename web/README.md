# BuildCv web

Next.js App Router client for `BuildCv.Api`, running as a **BFF**. Implements one screen —
`BuildCV Analysis.dc.html` from the Claude Design project — against the real `/v1` contract.

```bash
cp .env.example .env.local     # point BUILDCV_API_ORIGIN at the running API
npm install
npm run dev                    # http://localhost:3000
```

The API side, with the in-memory store:

```bash
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://localhost:5001 \
Persistence__Provider=InMemory \
Jwt__SigningKey='local-dev-signing-key-that-is-long-enough-32' \
dotnet run --project ../src/BuildCv.Api
```

## Why a BFF and not a SPA

`BuildCv.Api` authenticates a browser two ways: an `access_token` cookie, or an
`Authorization: Bearer` header. `CsrfGuardMiddleware` skips validation entirely when the
Authorization value is non-blank — bearer requests carry no ambient credential and are not
CSRF-able.

So the tokens live on the Next.js server, in two httpOnly cookies on **this** origin, and every call
to the API goes out server-side with a bearer header. That removes, in one move:

- `Cors:AllowedOrigins` and `AllowCredentials` configuration (there is no cross-origin request),
- the double-submit `X-XSRF-TOKEN` header,
- `GET /v1/auth/antiforgery` and its re-fetch-on-every-change-of-principal contract,
- the **401 → 403 flip** documented in `CLAUDE.md`: because the access cookie outlives the JWT it
  carries and the CSRF guard gates on cookie *presence*, an idle cookie client's next unsafe request
  to any non-exempt route answers `403 "CSRF validation failed."` rather than 401. Over bearer, a 401
  is a 401.

The JWT audience is `buildcv-bff`. This is the client that API was shaped for.

Refresh is **proactive**, off the token's own `exp` — a client that only reacts to 401 is the one
that breaks. `apiFetch` also retries once on a 401 as a safety net for clock skew.

All API traffic goes through route handlers under `src/app/api`, never a Server Component: Next.js
forbids writing cookies outside a Route Handler or Server Action, so a Server Component that
refreshed would throw the rotated refresh token away and kill the session at the next expiry.

## What the design asked for, and what the backend has

The source design is a mockup with invented data. Roughly a third of it has no data source. Nothing
below was faked; each was either mapped to the real thing or removed with the reason stated in the
code.

| Design | What shipped |
|---|---|
| CV picker with a score badge per CV | **Badge removed.** A match score needs a job posting; the readability score comes from a `POST` that creates a report, not a field on a list row. Shows last-edited, skill count and role count instead. |
| Paste JD → "Run analysis" | **A step was added.** `POST /v1/scoring/score` takes a `jobPostingId`. The only route from text to a posting carrying skill requirements is `/job-offers/extract` → review → `/job-offers/import`. |
| "Upload file" tab for the JD | **Removed.** No endpoint accepts a job posting as a file. |
| 4-step loading bar on a timer | **Two steps, both real** — the two round trips this screen makes. Nothing streams the engine's internal stages. |
| Breakdown: 4 fixed rows, fixed weights | **Driven by `breakdown.sections[]`** — six sections, weights renormalized per posting by the server. A section with weight 0 renders as *not measured*, never as a score. |
| "Strengths" prose bullets | **Sections that carried weight and scored ≥ 80**, with their real scores. No invented sentences. |
| "Gaps" prose bullets | **The top three `recommendations[]`** — the server's own sentences. |
| "18 of 24 keywords found", green chips | **Neutral chips, missing ones flagged red.** No green "found" chip: recommendations are capped at ten, so absence of a flag is not evidence of a match. Re-deriving the match here would also contradict the score — the engine recognises alternative spellings (`React.js` satisfies `React`). |
| "+3 pts" per suggestion | **Real** — `impact × 100`, and measured rather than estimated. |
| before/after diff | **Removed.** The API returns a sentence and an impact, no rewritten text. |
| "Apply to resume" / "Undo" | **Removed.** No endpoint edits a CV from a recommendation. The toggle only moves the projection, and says so. |
| CURRENT → PROJECTED | **Kept, with the caveat stated on screen**: impacts are each measured alone and are not guaranteed to add up. |
| — | **`isStale` added.** It has no slot in the design and it is the field that says the score describes a CV the candidate no longer has. |

Two rules the code follows that are worth not undoing:

- **Colour follows `analysis.band`, never a second set of thresholds.** The design coloured at
  90/70/50 while `Analysis.Band` cuts at 40/60/80 — keeping both would let the ring and the label
  disagree about one score.
- **Nothing re-implements a server rule.** No client-side skill matcher, no client-side band
  arithmetic. One statement per rule.

## Not implemented

The design project carries five more screens (`Auth`, `Dashboard`, `Editor`, `Settings`, `States`).
Only `Analysis` was in scope. `/login` exists because the analysis screen needs a session; there is
no sign-up screen, and accounts are created through `POST /v1/auth/register`.

The readability half of the product — `POST /v1/resumes/{id}/readability`, the second score the
README promises — is not on this screen and has no design to build from.
