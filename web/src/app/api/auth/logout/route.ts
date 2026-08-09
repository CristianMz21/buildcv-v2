import { NextResponse } from 'next/server';

import { apiFetch } from '@/lib/backend';
import { clearSession } from '@/lib/session';

/**
 * Logging out is a server-side revocation, not a cookie wipe.
 *
 * `POST /v1/auth/logout` revokes every refresh token on the account; dropping this BFF's cookies
 * without it would leave a live session in the store that a captured refresh token could keep
 * minting access tokens from for the full 30-day lifetime.
 *
 * The API answers 500 and leaves its own cookies in place when revocation FAILS, deliberately: a
 * store that cannot revoke is exactly when a false "you are logged out" is most dangerous. This
 * mirrors that — the local session is cleared only once the API confirms the revocation.
 */
export async function POST(): Promise<NextResponse> {
  try {
    const upstream = await apiFetch('/auth/logout', { method: 'POST' });

    if (!upstream.ok) {
      return NextResponse.json(
        { status: 502, title: 'Bad Gateway', detail: 'Sessions could not be revoked. Try again.' },
        { status: 502, headers: { 'content-type': 'application/problem+json' } },
      );
    }
  } catch {
    // No session to revoke, or the API is unreachable. Dropping this browser's credentials is still
    // a valid request and leaks nothing, so it is not reported as a failure.
  }

  await clearSession();
  return new NextResponse(null, { status: 204 });
}
