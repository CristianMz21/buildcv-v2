/**
 * TypeScript mirrors of the BuildCv.Api `/v1` wire contracts.
 *
 * Hand-written, field for field, against `src/BuildCv.Api/Contracts/*.cs` — not generated. That is
 * the same bargain the C# DTOs themselves take: the mapping is declared in one file so a refactor on
 * the other side cannot reshape it by accident, and a swapped field here is a real bug rather than a
 * serializer's opinion.
 *
 * Two encodings are settled across the whole of v1 and are relied on here: every id is a bare
 * `string` guid (never `{ value }`), and every enum is its NAME (`"Skills"`, never `0`). The C#
 * comments call this the v1 settlement, and `V1ContractShapeTests` walks each route's real body to
 * enforce it. Property names arrive camelCased by System.Text.Json's default policy.
 */

// ── Pagination ─────────────────────────────────────────────────────────────────

/** `nextCursor` is null on the last page and is the ONLY thing a client may use to ask for more. */
export interface PagedResponse<T> {
  items: T[];
  nextCursor: string | null;
}

// ── Identity ───────────────────────────────────────────────────────────────────

export interface TokenResponse {
  accessToken: string;
  /** Seconds. The value a client schedules its proactive refresh off. */
  expiresIn: number;
}

export interface AccountResponse {
  id: string;
  email: string;
  role: string;
  status: string;
  isEmailVerified: boolean;
  createdAt: string;
  lastLoginAt: string | null;
}

// ── Resumes ────────────────────────────────────────────────────────────────────

export interface DateRangeResponse {
  start: string;
  end: string | null;
}

export interface ProfileResponse {
  network: string;
  username: string | null;
  url: string | null;
}

export interface ContactInformationResponse {
  fullName: string;
  email: string;
  phoneNumber: string | null;
  location: string | null;
  website: string | null;
  summary: string | null;
  profiles: ProfileResponse[];
}

export interface SkillResponse {
  name: string;
  level: string | null;
  yearsOfExperience: number | null;
  keywords: string[];
}

/** `type` is the ExperienceType name; only `"Professional"` entries count toward the experience score. */
export interface ExperienceResponse {
  type: string;
  organization: string;
  position: string;
  period: DateRangeResponse;
  summary: string | null;
  highlights: string[];
}

/**
 * The full CV graph. `GET /v1/resumes` returns these in a page — the API has no lightweight
 * projection for a list, so the picker on the analysis screen pays for the whole aggregate. Only the
 * fields this client actually reads are declared; the rest of the collections are present on the
 * wire and deliberately left untyped here rather than mirrored half-way.
 *
 * **A Resume carries no name of its own.** There is no title, label or nickname on the aggregate, so
 * the picker cannot show one and does not invent one — see `resumeLabel` in `format.ts` for what it
 * shows instead and why.
 */
export interface ResumeResponse {
  id: string;
  ownerId: string;
  contactInformation: ContactInformationResponse;
  createdAt: string;
  updatedAt: string;
  experiences: ExperienceResponse[];
  skills: SkillResponse[];
}

// ── Job postings ───────────────────────────────────────────────────────────────

export interface JobRequirementResponse {
  skill: string;
  priority: RequirementPriority;
  weight: number;
}

export type RequirementPriority = 'MustHave' | 'NiceToHave' | (string & {});

export interface JobPostingResponse {
  id: string;
  ownerId: string;
  title: string;
  description: string | null;
  companyId: string | null;
  companyName: string | null;
  status: string;
  createdAt: string;
  updatedAt: string;
  publishedAt: string | null;
  closesAt: string | null;
  requirements: JobRequirementResponse[];
  responsibilities: { description: string }[];
  languageRequirements: { name: string; minimumLevel: string }[];
  educationLevel: string | null;
}

/**
 * One skill requirement proposed from pasted offer text by `POST /v1/job-offers/extract`.
 *
 * `priorityGuessed` is why the confirmation step exists at all: it says the priority is a default
 * this API promoted, not something it read out of the text. A UI that posts the proposal straight
 * back to `/job-offers/import` is silently accepting guesses on the candidate's behalf.
 */
export interface ProposedRequirementResponse {
  skill: string;
  priority: RequirementPriority;
  priorityGuessed: boolean;
}

export interface ExtractJobOfferRequirementsResponse {
  requirements: ProposedRequirementResponse[];
}

// ── Scoring ────────────────────────────────────────────────────────────────────

/** The six sections a match score is broken down into. Closed at six; see `SectionType.cs`. */
export type SectionName =
  | 'Skills'
  | 'Experience'
  | 'Education'
  | 'Certifications'
  | 'Projects'
  | 'Languages';

export type ScoreBand = 'Low' | 'Medium' | 'Good' | 'Strong' | (string & {});

export type RecommendationPriority = 'Critical' | 'Important' | 'NiceToHave' | (string & {});

/** `RecommendationKind.cs`. Append-only on the server; unknown values must not crash a client. */
export type RecommendationKind =
  | 'MissingMustHaveSkill'
  | 'MissingNiceToHaveSkill'
  | 'NoEducationRecorded'
  | 'NoDegreeRecorded'
  | 'FewerCertificationsThanExpected'
  | 'FewerProjectsThanExpected'
  | 'LanguageMissing'
  | 'LanguageBelowRequiredLevel'
  | 'LanguageLevelNotRecorded'
  | 'ExperienceNotMarkedProfessional'
  | (string & {});

/**
 * One section's score paired with the weight it carried.
 *
 * **The pairing is the contract.** `score` is 0..1 and is MEANINGLESS when `weight` is 0: the posting
 * expressed no weighted requirement for that section, so nothing was measured and a client must not
 * render the number as a result. There is deliberately no separate "applicable" flag — the weight is
 * the signal, so the two can never disagree.
 */
export interface SectionScoreResponse {
  section: SectionName;
  score: number;
  weight: number;
}

/** Weights total 1.0 after the zero-weighted sections are renormalized out. */
export interface ScoringWeightsResponse {
  skills: number;
  experience: number;
  education: number;
  certifications: number;
  projects: number;
  languages: number;
  /** Names the scoring MODEL — weights and formulas together. Two versions are not comparable. */
  schemaVersion: number;
}

export interface ScoreBreakdownResponse {
  skillsScore: number;
  experienceScore: number;
  educationScore: number;
  certificationsScore: number;
  projectsScore: number;
  languagesScore: number;
  weights: ScoringWeightsResponse;
  weightedTotal: number;
  sections: SectionScoreResponse[];
}

/**
 * `impact` is on the 0..1 scale, NOT the 0..100 scale `overallScore` uses — a client showing it as
 * points multiplies by 100. It is measured rather than estimated: the exact increase in
 * `weightedTotal` produced by re-evaluating the same formula with that one gap closed.
 */
export interface RecommendationResponse {
  section: SectionName;
  priority: RecommendationPriority;
  kind: RecommendationKind;
  message: string;
  impact: number;
}

export interface AnalysisResponse {
  id: string;
  breakdown: ScoreBreakdownResponse;
  resumeId: string;
  jobPostingId: string;
  scoredAt: string;
  recommendations: RecommendationResponse[];
  /** 0..100. A fact about the (resume, posting) PAIR — never add it to a readability score. */
  overallScore: number;
  band: ScoreBand;
  /**
   * Computed per request, never stored. True when the resume has been edited since this score was
   * taken, and ALSO true when the analysis predates the provenance columns and cannot say which
   * version it scored. `POST /v1/scoring/score` always answers false.
   */
  isStale: boolean;
}

// ── Errors ─────────────────────────────────────────────────────────────────────

/**
 * Every error this API emits is ProblemDetails-shaped, with two measured exceptions documented in
 * CLAUDE.md: the 413 Kestrel tears down before any handler runs, and a malformed multipart body on
 * the resume-extract route. Neither is reachable from this client.
 */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  /** Present on field-error responses, keyed by field path (`requirements[2].skill`). */
  errors?: Record<string, string[]>;
}
