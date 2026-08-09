import type { NextResponse } from 'next/server';

import { apiPost } from '@/lib/backend';
import { relay, withSession } from '@/lib/relay';

/**
 * Pasted offer text in, proposed skill requirements out. Writes nothing.
 *
 * This is step one of the two that stand between "paste a job description" and a score. It is the
 * ONLY way to get skill requirements onto a posting from this client, and therefore the only way to
 * get a nonzero `weights.skills` on the resulting analysis — a posting created through `POST
 * /v1/jobs` carries a title, a company and a description, and nothing for the skills section to
 * measure.
 */
export async function POST(request: Request): Promise<NextResponse> {
  return withSession(async () => {
    const { text } = (await request.json()) as { text?: string };
    return relay(await apiPost('/job-offers/extract', { text }));
  });
}
