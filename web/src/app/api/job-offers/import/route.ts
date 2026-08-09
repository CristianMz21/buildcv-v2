import type { NextResponse } from 'next/server';

import { apiPost } from '@/lib/backend';
import { relay, withSession } from '@/lib/relay';

/**
 * Step two: turns the reviewed requirements into a candidate-owned Draft job offer and returns the
 * posting — whose `id` is what `POST /v1/scoring/score` needs.
 *
 * The body is forwarded as received, including the requirement priorities as STRINGS. Every field of
 * `ImportJobOfferRequest` is a nullable string on purpose: a malformed value has to come back as a
 * field error carrying a path (`requirements[2].skill`) that the review form can attach to the right
 * input, not as a bare model-binding 400 that names nothing. Coercing anything here would move that
 * failure to the binding boundary and lose the path.
 */
export async function POST(request: Request): Promise<NextResponse> {
  return withSession(async () => relay(await apiPost('/job-offers/import', await request.json())));
}
