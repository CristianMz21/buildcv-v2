import 'server-only';

import { NextResponse } from 'next/server';

import { NoSessionError } from './backend';

/**
 * Hands an upstream BuildCv.Api response back to the browser unchanged.
 *
 * STATUS AND BODY ARE PASSED THROUGH, not reinterpreted. The API's error bodies are already
 * ProblemDetails — including the field-error `errors` object that `/job-offers/import` uses to name
 * a bad requirement by path — and a BFF that flattened them into its own shape would throw away the
 * only thing a form needs to mark the right input. The `content-type` is carried across for the same
 * reason: `application/problem+json` is half the API's error contract.
 */
export async function relay(upstream: Response): Promise<NextResponse> {
  const body = await upstream.text();

  return new NextResponse(body.length > 0 ? body : null, {
    status: upstream.status,
    headers: {
      'content-type': upstream.headers.get('content-type') ?? 'application/json',
    },
  });
}

/**
 * Wraps a route handler so a missing or unrecoverable session becomes a plain 401.
 *
 * The browser can trust a 401 from this BFF, which is precisely what it cannot do against the API
 * directly: there, an idle cookie client's next unsafe request answers 403 "CSRF validation failed."
 * because the access cookie outlives the JWT it carries. Going out over bearer removes that flip, so
 * "401 means sign in again" is true here.
 */
export async function withSession(handler: () => Promise<NextResponse>): Promise<NextResponse> {
  try {
    return await handler();
  } catch (error) {
    if (error instanceof NoSessionError) {
      return NextResponse.json(
        { status: 401, title: 'Unauthorized', detail: 'Your session has ended. Sign in again.' },
        { status: 401, headers: { 'content-type': 'application/problem+json' } },
      );
    }

    throw error;
  }
}
