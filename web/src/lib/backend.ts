import 'server-only';

import type { ProblemDetails, TokenResponse } from './contracts';
import { readSession, secondsUntilExpiry, writeSession, type Session } from './session';

/**
 * The only place this app talks to BuildCv.Api.
 *
 * Every call goes out with `Authorization: Bearer`, which is what makes the whole cookie/CSRF
 * apparatus on the other side inapplicable — see the note in `session.ts`. Nothing here ever runs in
 * the browser.
 */

const API_ORIGIN = (process.env.BUILDCV_API_ORIGIN ?? 'http://localhost:5001').replace(/\/+$/, '');

/**
 * The ASP.NET Core development certificate is self-signed, and Node rejects it. This is the blunt
 * escape hatch for pointing local dev at the https listener; it is refused in production so a
 * mis-set variable cannot disable certificate validation on a deployed host.
 */
if (process.env.BUILDCV_ALLOW_SELF_SIGNED === '1') {
  if (process.env.NODE_ENV === 'production') {
    throw new Error('BUILDCV_ALLOW_SELF_SIGNED must not be set in production.');
  }
  process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
}

/** Refresh this far ahead of `exp` rather than waiting for a 401 — the client contract in CLAUDE.md. */
const REFRESH_SKEW_SECONDS = 60;

const REFRESH_COOKIE_NAME = 'refresh_token';

export class NoSessionError extends Error {
  constructor() {
    super('No BuildCv session.');
    this.name = 'NoSessionError';
  }
}

function apiUrl(path: string): string {
  if (!path.startsWith('/')) throw new Error(`API path must be absolute: ${path}`);
  return `${API_ORIGIN}/v1${path}`;
}

/**
 * Pulls the opaque refresh token out of the API's `Set-Cookie` headers.
 *
 * The API only ever delivers it as an httpOnly cookie scoped to `/v1/auth/refresh`; there is no
 * field for it in any response body. An empty value means the API cleared it — a logout or a
 * revocation — and is reported as absent rather than stored as the string "".
 */
function refreshTokenFrom(response: Response): string | null {
  for (const header of response.headers.getSetCookie()) {
    const [pair] = header.split(';');
    if (!pair) continue;

    const separator = pair.indexOf('=');
    if (separator === -1) continue;

    if (pair.slice(0, separator).trim() !== REFRESH_COOKIE_NAME) continue;

    const value = pair.slice(separator + 1).trim();
    return value.length > 0 ? value : null;
  }

  return null;
}

async function readProblem(response: Response): Promise<ProblemDetails> {
  try {
    return (await response.json()) as ProblemDetails;
  } catch {
    return { status: response.status, title: 'Unexpected error', detail: response.statusText };
  }
}

export type LoginOutcome =
  | { ok: true; session: Session; expiresIn: number }
  | { ok: false; status: number; problem: ProblemDetails };

/**
 * Exchanges credentials for a session. `/v1/auth/login` is `AllowAnonymous` and CSRF-exempt, and it
 * returns the access token in the body while delivering the refresh token as a cookie — so both
 * halves have to be collected from different places.
 */
export async function login(email: string, password: string): Promise<LoginOutcome> {
  const response = await fetch(apiUrl('/auth/login'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
    cache: 'no-store',
  });

  if (!response.ok) {
    return { ok: false, status: response.status, problem: await readProblem(response) };
  }

  const token = (await response.json()) as TokenResponse;
  const refreshToken = refreshTokenFrom(response);

  if (!refreshToken) {
    // Reachable only if the API stops setting the cookie. Failing loudly beats storing half a
    // session that dies silently at the first access-token expiry.
    return {
      ok: false,
      status: 502,
      problem: { status: 502, title: 'Bad gateway', detail: 'The API issued no refresh token.' },
    };
  }

  return {
    ok: true,
    session: { accessToken: token.accessToken, refreshToken },
    expiresIn: token.expiresIn,
  };
}

/**
 * Trades the stored refresh token for a fresh pair, persisting both.
 *
 * The refresh token ROTATES — the API issues a new one on every successful refresh — so failing to
 * store what comes back would strand the session at the next expiry.
 */
async function refresh(session: Session): Promise<Session | null> {
  const response = await fetch(apiUrl('/auth/refresh'), {
    method: 'POST',
    // Sent as a header rather than through a cookie jar: this is a server-to-server call with no
    // browser in it, and the API reads the value straight off Request.Cookies.
    headers: { Cookie: `${REFRESH_COOKIE_NAME}=${session.refreshToken}` },
    cache: 'no-store',
  });

  if (!response.ok) return null;

  const token = (await response.json()) as TokenResponse;
  const rotated = refreshTokenFrom(response) ?? session.refreshToken;
  const refreshed: Session = { accessToken: token.accessToken, refreshToken: rotated };

  await writeSession(refreshed);
  return refreshed;
}

/**
 * Calls BuildCv.Api on behalf of the signed-in account.
 *
 * Refresh is PROACTIVE, driven by the token's own `exp`, which is the contract CLAUDE.md states for
 * this API. The reactive 401 retry below is a safety net for clock skew and for a token revoked
 * mid-flight, not the primary mechanism — a client that only reacts to 401 is the one that breaks.
 *
 * @throws NoSessionError when there is nothing to authenticate with. Route handlers turn this into a
 *   401 so the browser knows to send the user back to the sign-in screen.
 */
export async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  let session = await readSession();
  if (!session) throw new NoSessionError();

  if (secondsUntilExpiry(session.accessToken) < REFRESH_SKEW_SECONDS) {
    const refreshed = await refresh(session);
    if (!refreshed) throw new NoSessionError();
    session = refreshed;
  }

  const send = (bearer: string) =>
    fetch(apiUrl(path), {
      ...init,
      headers: { ...init.headers, Authorization: `Bearer ${bearer}` },
      cache: 'no-store',
    });

  const response = await send(session.accessToken);
  if (response.status !== 401) return response;

  const retried = await refresh(session);
  if (!retried) throw new NoSessionError();

  return send(retried.accessToken);
}

/** `apiFetch` for JSON bodies, with the two headers every write needs. */
export function apiPost(path: string, body: unknown): Promise<Response> {
  return apiFetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify(body),
  });
}

export { apiUrl };
