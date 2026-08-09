import 'server-only';

import { cookies } from 'next/headers';

/**
 * The BFF's own session, held in two httpOnly cookies on the Next.js origin.
 *
 * WHY THE BFF HOLDS THE TOKENS AT ALL. BuildCv.Api authenticates a browser two ways: an
 * `access_token` cookie, or an `Authorization: Bearer` header. `CsrfGuardMiddleware` skips
 * validation entirely when the Authorization value is non-blank — bearer requests carry no ambient
 * credential and are not CSRF-able — so a server-side caller holding the token bypasses the whole
 * double-submit dance: no `GET /v1/auth/antiforgery`, no `X-XSRF-TOKEN`, no re-fetch on every change
 * of principal, and none of the 401 -> 403 flip that CLAUDE.md documents for idle cookie clients.
 * The JWT audience is literally `buildcv-bff`; this is the client that API was shaped for.
 *
 * WHY THE COOKIES ARE NOT ENCRYPTED. They hold exactly what BuildCv.Api itself puts in a browser
 * cookie: the raw JWT and the opaque refresh token. Sealing them here would buy nothing over the
 * status quo on the other side, and hand-rolled session crypto is a worse bet than httpOnly +
 * SameSite. What this DOES buy is that the credentials live on the BFF's origin rather than the
 * API's, so no page script anywhere can reach them.
 */

const ACCESS_COOKIE = 'bcv_access';
const REFRESH_COOKIE = 'bcv_refresh';

/** Mirrors the API's own refresh-token lifetime (`Jwt:RefreshTokenDays`, 30 by default). */
const SESSION_MAX_AGE_SECONDS = 30 * 24 * 60 * 60;

export interface Session {
  accessToken: string;
  refreshToken: string;
}

const cookieOptions = {
  httpOnly: true,
  // Lax rather than Strict: the login redirect is a top-level GET navigation, and Strict would drop
  // the cookie on a link followed in from outside the app, logging the user out for no gain. Every
  // state-changing call is a same-origin fetch from our own pages, which Lax covers.
  sameSite: 'lax',
  secure: process.env.NODE_ENV === 'production',
  path: '/',
  maxAge: SESSION_MAX_AGE_SECONDS,
} as const;

export async function readSession(): Promise<Session | null> {
  const jar = await cookies();
  const accessToken = jar.get(ACCESS_COOKIE)?.value;
  const refreshToken = jar.get(REFRESH_COOKIE)?.value;

  if (!accessToken || !refreshToken) return null;
  return { accessToken, refreshToken };
}

/** Only callable from a Route Handler or Server Action — Next.js forbids writing cookies elsewhere. */
export async function writeSession(session: Session): Promise<void> {
  const jar = await cookies();
  jar.set(ACCESS_COOKIE, session.accessToken, cookieOptions);
  jar.set(REFRESH_COOKIE, session.refreshToken, cookieOptions);
}

export async function clearSession(): Promise<void> {
  const jar = await cookies();
  jar.set(ACCESS_COOKIE, '', { ...cookieOptions, maxAge: 0 });
  jar.set(REFRESH_COOKIE, '', { ...cookieOptions, maxAge: 0 });
}

/**
 * Seconds until the access token's own `exp`, or 0 when it cannot be read.
 *
 * The payload is decoded, NOT verified — this side has no signing key and does not need one. The
 * only decision it drives is "refresh before spending a request", and BuildCv.Api validates the
 * signature on every call regardless. A token this function misreads costs one extra refresh, never
 * an accepted forgery.
 */
export function secondsUntilExpiry(accessToken: string): number {
  const payload = accessToken.split('.')[1];
  if (!payload) return 0;

  try {
    const json = Buffer.from(payload, 'base64url').toString('utf8');
    const claims = JSON.parse(json) as { exp?: number };
    if (typeof claims.exp !== 'number') return 0;
    return claims.exp - Math.floor(Date.now() / 1000);
  } catch {
    return 0;
  }
}
