import type {
  RecommendationPriority,
  RecommendationResponse,
  ResumeResponse,
  ScoreBand,
  SectionName,
} from './contracts';

/** A tone drawn from the palette in `globals.css`. */
export interface Tone {
  fg: string;
  bg: string;
  border: string;
}

const TONES = {
  strong: { fg: 'var(--good-strong)', bg: 'var(--good-bg)', border: 'var(--good-border)' },
  good: { fg: 'var(--primary)', bg: 'var(--primary-bg)', border: '#dbeafe' },
  medium: { fg: 'var(--warn)', bg: 'var(--warn-bg)', border: '#fde68a' },
  low: { fg: 'var(--bad)', bg: 'var(--bad-bg)', border: 'var(--bad-border)' },
  neutral: { fg: 'var(--fg-muted)', bg: 'var(--border-soft)', border: 'var(--border)' },
} as const satisfies Record<string, Tone>;

/**
 * Colour follows the SERVER'S band, not a second set of thresholds invented here.
 *
 * The source design coloured scores at 90/70/50 while `Analysis.Band` cuts at 40/60/80. Keeping the
 * design's numbers would have let the swatch and the label disagree about the same score — a green
 * ring beside the word "Good" — which is the classic way one rule written twice drifts apart.
 */
export function bandTone(band: ScoreBand): Tone {
  switch (band) {
    case 'Strong':
      return TONES.strong;
    case 'Good':
      return TONES.good;
    case 'Medium':
      return TONES.medium;
    case 'Low':
      return TONES.low;
    default:
      return TONES.neutral;
  }
}

/**
 * What the band means, in the API's own terms.
 *
 * Deliberately NOT the source design's "Above 70 typically passes automated screening" — nothing in
 * this system measures what an external ATS does with a score, so that sentence would be a claim the
 * product cannot stand behind. These describe the cut-offs, which it can.
 */
export function bandCopy(band: ScoreBand): string {
  switch (band) {
    case 'Strong':
      return 'Strong match — 80 or above';
    case 'Good':
      return 'Good match — 60 to 79';
    case 'Medium':
      return 'Partial match — 40 to 59';
    case 'Low':
      return 'Weak match — below 40';
    default:
      return band;
  }
}

/** A section score is 0..1; the ring and the bars are percentages. */
export function toPercent(unitValue: number): number {
  return Math.round(unitValue * 100);
}

/**
 * `impact` is on the 0..1 scale, so points are `impact × 100`.
 *
 * Kept to one decimal rather than rounded to whole points, because the API's claim about this number
 * is that it is MEASURED — the exact increase from closing that one gap. Real values land on 11.1,
 * 7.7, 3.8; rounding 3.8 to "+4 pts" overstates a figure whose selling point is that it was not
 * estimated. A trailing `.0` is dropped so a clean 3 does not read as suspiciously precise.
 *
 * An impact of 0 is a real answer, not a bug: a posting can state requirements and weight every one
 * of them 0.0, which renormalizes the section out while the advice naming those requirements stays.
 */
export function impactPoints(impact: number): string {
  const points = Number((impact * 100).toFixed(1));
  if (points <= 0) return '0 pts';
  return `+${points} ${points === 1 ? 'pt' : 'pts'}`;
}

export function priorityTone(priority: RecommendationPriority): Tone {
  switch (priority) {
    case 'Critical':
      return TONES.low;
    case 'Important':
      return TONES.medium;
    case 'NiceToHave':
      return TONES.neutral;
    default:
      return TONES.neutral;
  }
}

export function priorityLabel(priority: RecommendationPriority): string {
  return priority === 'NiceToHave' ? 'Nice to have' : priority;
}

const SECTION_LABELS: Record<SectionName, string> = {
  Skills: 'Skills',
  Experience: 'Experience',
  Education: 'Education',
  Certifications: 'Certifications',
  Projects: 'Projects',
  Languages: 'Languages',
};

/** Unknown names pass through: `SectionType` is append-only server-side and this must not crash. */
export function sectionLabel(section: SectionName | string): string {
  return SECTION_LABELS[section as SectionName] ?? section;
}

/**
 * What to call a CV in a list.
 *
 * `Resume` has NO name, title or label — the aggregate simply does not carry one, so there is
 * nothing to read and nothing worth inventing. The most recent position is the closest honest stand-in
 * (it is what distinguishes one of a candidate's CVs from another), with the contact name as the
 * fallback for a CV that has no work history yet.
 */
export function resumeLabel(resume: ResumeResponse): string {
  const mostRecent = [...resume.experiences].sort((a, b) =>
    b.period.start.localeCompare(a.period.start),
  )[0];

  return mostRecent?.position ?? resume.contactInformation.fullName;
}

/** English-only, and only for the regular nouns this screen counts. */
export function plural(count: number, singular: string): string {
  return `${count} ${singular}${count === 1 ? '' : 's'}`;
}

const RELATIVE = new Intl.RelativeTimeFormat('en', { numeric: 'auto' });

const UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 60 * 60 * 1000],
  ['month', 30 * 24 * 60 * 60 * 1000],
  ['day', 24 * 60 * 60 * 1000],
  ['hour', 60 * 60 * 1000],
  ['minute', 60 * 1000],
];

export function relativeTime(iso: string, now: number = Date.now()): string {
  const elapsed = new Date(iso).getTime() - now;
  if (Number.isNaN(elapsed)) return 'unknown';

  for (const [unit, ms] of UNITS) {
    if (Math.abs(elapsed) >= ms) return RELATIVE.format(Math.round(elapsed / ms), unit);
  }

  return 'just now';
}

/**
 * Whether the server flagged this exact requirement as missing.
 *
 * The check is CONTAINMENT OF THE REQUIREMENT'S OWN SKILL STRING in a `Missing*Skill` recommendation,
 * not a re-implementation of the matcher. That matters: the scoring engine recognises alternative
 * spellings — `"React.js"` satisfies a requirement for `"React"` — and a comparison written here
 * against the resume's skill list would disagree with the score sitting next to it. Two statements of
 * one rule is how they drift.
 *
 * A recommendation whose wording changes stops matching and the chip reads as UNVERIFIED rather than
 * as found, which is the direction that cannot mislead.
 */
export function missingSkills(recommendations: RecommendationResponse[]): (skill: string) => boolean {
  const messages = recommendations
    .filter((r) => r.kind === 'MissingMustHaveSkill' || r.kind === 'MissingNiceToHaveSkill')
    .map((r) => r.message);

  return (skill: string) => messages.some((message) => message.includes(`'${skill}'`));
}
