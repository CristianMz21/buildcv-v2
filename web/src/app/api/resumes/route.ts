import type { NextRequest, NextResponse } from 'next/server';

import { apiFetch } from '@/lib/backend';
import { relay, withSession } from '@/lib/relay';

/**
 * The resume picker's data source.
 *
 * `limit` and `cursor` are forwarded rather than fixed here. There are no unbounded list methods on
 * any BuildCv repository — every list is keyset-paginated, `limit` is clamped into 1..100 server-side,
 * and a cursor that will not decode is a 400 rather than a silent restart at page one. Swallowing
 * either parameter would make the second page unreachable.
 */
export async function GET(request: NextRequest): Promise<NextResponse> {
  return withSession(async () => {
    const incoming = request.nextUrl.searchParams;
    const forwarded = new URLSearchParams();

    const limit = incoming.get('limit');
    const cursor = incoming.get('cursor');
    if (limit) forwarded.set('limit', limit);
    if (cursor) forwarded.set('cursor', cursor);

    const query = forwarded.size > 0 ? `?${forwarded}` : '';
    return relay(await apiFetch(`/resumes${query}`, { headers: { Accept: 'application/json' } }));
  });
}
