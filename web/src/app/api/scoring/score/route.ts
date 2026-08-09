import type { NextResponse } from 'next/server';

import { apiPost } from '@/lib/backend';
import { relay, withSession } from '@/lib/relay';

/**
 * Step three: the score itself.
 *
 * DE-DUPLICATED SERVER-SIDE. If this resume was already scored against this posting today, neither
 * has been edited since, and the scoring model has not moved, the stored run comes back — same `id`,
 * same `scoredAt`, no new entry in the history. A repeat is not a no-op and not an error: it is the
 * same scoring event. Nothing here retries or works around that.
 */
export async function POST(request: Request): Promise<NextResponse> {
  return withSession(async () => {
    const { resumeId, jobPostingId } = (await request.json()) as {
      resumeId?: string;
      jobPostingId?: string;
    };
    return relay(await apiPost('/scoring/score', { resumeId, jobPostingId }));
  });
}
