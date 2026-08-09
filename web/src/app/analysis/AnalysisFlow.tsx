'use client';

import { useRouter } from 'next/navigation';
import { useCallback, useEffect, useRef, useState } from 'react';

import { ChevronRight } from '@/components/icons';
import type {
  AnalysisResponse,
  ExtractJobOfferRequirementsResponse,
  JobPostingResponse,
  PagedResponse,
  ProblemDetails,
  ResumeResponse,
} from '@/lib/contracts';
import { resumeLabel } from '@/lib/format';

import styles from './analysis.module.css';
import { InputsStep } from './InputsStep';
import { RequirementsStep, type DraftRequirement } from './RequirementsStep';
import { ResultsStep } from './ResultsStep';
import { RunningStep } from './RunningStep';
import { SuggestionsStep } from './SuggestionsStep';

type Phase = 'inputs' | 'requirements' | 'running' | 'results' | 'suggestions';

const STEP_LABELS: [string, Phase[]][] = [
  ['1 · Inputs', ['inputs']],
  ['2 · Requirements', ['requirements']],
  ['3 · Results', ['running', 'results', 'suggestions']],
];

/** The BFF answers a real 401 when the session is gone — see the note in `relay.ts`. */
class SessionExpired extends Error {}

async function readJson<T>(response: Response): Promise<T> {
  if (response.status === 401) throw new SessionExpired();

  if (!response.ok) {
    const problem = (await response.json().catch(() => ({}))) as ProblemDetails;
    const error = new Error(problem.detail ?? problem.title ?? `Request failed (${response.status}).`);
    // Field errors are carried on the thrown error rather than flattened into the message: the
    // requirement form needs the path to mark the right input, which a sentence cannot give it.
    Object.assign(error, { fieldErrors: problem.errors ?? {} });
    throw error;
  }

  return (await response.json()) as T;
}

function fieldErrorsOf(error: unknown): Record<string, string[]> {
  const carried = (error as { fieldErrors?: Record<string, string[]> } | null)?.fieldErrors;
  return carried ?? {};
}

export function AnalysisFlow() {
  const router = useRouter();

  const [phase, setPhase] = useState<Phase>('inputs');
  const [runStep, setRunStep] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  const [resumes, setResumes] = useState<ResumeResponse[] | null>(null);
  const [resumesError, setResumesError] = useState<string | null>(null);
  const [selectedResumeId, setSelectedResumeId] = useState<string | null>(null);

  const [jobDescription, setJobDescription] = useState('');
  const [title, setTitle] = useState('');
  const [companyName, setCompanyName] = useState('');
  const [requirements, setRequirements] = useState<DraftRequirement[]>([]);

  const [posting, setPosting] = useState<JobPostingResponse | null>(null);
  const [analysis, setAnalysis] = useState<AnalysisResponse | null>(null);
  const [counted, setCounted] = useState<ReadonlySet<number>>(new Set());

  // A plain counter rather than a random id: keys only have to be unique within this list, and a
  // deterministic source keeps a server/client render from ever disagreeing about one.
  const nextKey = useRef(0);
  const makeKey = () => `req-${nextKey.current++}`;

  const onExpired = useCallback(() => router.replace('/login'), [router]);

  useEffect(() => {
    let cancelled = false;

    (async () => {
      try {
        // The first page only. Paging exists on this route and the cursor is forwarded by the BFF;
        // a "load more" control belongs here the day a candidate has more than a screenful of CVs.
        const page = await readJson<PagedResponse<ResumeResponse>>(
          await fetch('/api/resumes?limit=20'),
        );

        if (cancelled) return;
        setResumes(page.items);
        setSelectedResumeId((current) => current ?? page.items[0]?.id ?? null);
      } catch (caught) {
        if (cancelled) return;
        if (caught instanceof SessionExpired) return onExpired();
        setResumesError(caught instanceof Error ? caught.message : 'Could not load your CVs.');
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [onExpired]);

  async function extract() {
    setBusy(true);
    setError(null);
    setFieldErrors({});

    try {
      const proposal = await readJson<ExtractJobOfferRequirementsResponse>(
        await fetch('/api/job-offers/extract', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ text: jobDescription }),
        }),
      );

      setRequirements(
        proposal.requirements.map((proposed) => ({
          key: makeKey(),
          skill: proposed.skill,
          priority: proposed.priority,
          guessed: proposed.priorityGuessed,
        })),
      );
      setPhase('requirements');
    } catch (caught) {
      if (caught instanceof SessionExpired) return onExpired();
      setError(caught instanceof Error ? caught.message : 'Could not read that posting.');
    } finally {
      setBusy(false);
    }
  }

  async function run() {
    setBusy(true);
    setError(null);
    setFieldErrors({});
    setRunStep(0);
    setPhase('running');

    try {
      const created = await readJson<JobPostingResponse>(
        await fetch('/api/job-offers/import', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            title,
            companyName,
            requirements: requirements
              .filter((requirement) => requirement.skill.trim() !== '')
              .map((requirement) => ({
                skill: requirement.skill,
                priority: requirement.priority,
              })),
          }),
        }),
      );

      setPosting(created);
      setRunStep(1);

      const scored = await readJson<AnalysisResponse>(
        await fetch('/api/scoring/score', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ resumeId: selectedResumeId, jobPostingId: created.id }),
        }),
      );

      setAnalysis(scored);
      setCounted(new Set());
      setPhase('results');
    } catch (caught) {
      if (caught instanceof SessionExpired) return onExpired();
      setFieldErrors(fieldErrorsOf(caught));
      setError(caught instanceof Error ? caught.message : 'The analysis failed.');
      // Back to the form that owns the inputs, so a field error lands next to its input rather than
      // on a progress screen with nothing to correct.
      setPhase('requirements');
    } finally {
      setBusy(false);
    }
  }

  function restart() {
    setPhase('inputs');
    setPosting(null);
    setAnalysis(null);
    setRequirements([]);
    setCounted(new Set());
    setError(null);
    setFieldErrors({});
  }

  async function signOut() {
    await fetch('/api/auth/logout', { method: 'POST' });
    router.replace('/login');
    router.refresh();
  }

  const selectedResume = resumes?.find((resume) => resume.id === selectedResumeId) ?? null;

  return (
    <div className={styles.shell}>
      <header className={styles.header}>
        <span className={styles.mark} aria-hidden="true">
          B
        </span>
        <span className={styles.headerTitle}>New analysis</span>

        <nav className={styles.stepper} aria-label="Progress">
          {STEP_LABELS.map(([label, phases], index) => (
            <span key={label} style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
              {index > 0 && <ChevronRight size={12} />}
              <span
                className={`${styles.step} ${phases.includes(phase) ? styles.stepCurrent : ''}`}
                aria-current={phases.includes(phase) ? 'step' : undefined}
              >
                {label}
              </span>
            </span>
          ))}
        </nav>

        <button
          type="button"
          className="btn btnGhost"
          onClick={signOut}
          style={{ marginLeft: 'auto' }}
        >
          Sign out
        </button>
      </header>

      <main className={styles.main}>
        {phase === 'inputs' && (
          <>
            {error && (
              <div className={`${styles.notice} ${styles.noticeError}`} role="alert">
                {error}
              </div>
            )}
            <InputsStep
              resumes={resumes}
              resumesError={resumesError}
              selectedResumeId={selectedResumeId}
              onSelectResume={setSelectedResumeId}
              jobDescription={jobDescription}
              onJobDescriptionChange={setJobDescription}
              busy={busy}
              onExtract={extract}
            />
          </>
        )}

        {phase === 'requirements' && (
          <RequirementsStep
            title={title}
            companyName={companyName}
            requirements={requirements}
            fieldErrors={fieldErrors}
            error={error}
            busy={busy}
            onTitleChange={setTitle}
            onCompanyChange={setCompanyName}
            onRequirementChange={(key, patch) =>
              setRequirements((current) =>
                current.map((requirement) =>
                  requirement.key === key ? { ...requirement, ...patch } : requirement,
                ),
              )
            }
            onRemoveRequirement={(key) =>
              setRequirements((current) => current.filter((requirement) => requirement.key !== key))
            }
            onAddRequirement={() =>
              setRequirements((current) => [
                ...current,
                { key: makeKey(), skill: '', priority: 'MustHave', guessed: false },
              ])
            }
            onBack={() => setPhase('inputs')}
            onRun={run}
          />
        )}

        {phase === 'running' && <RunningStep step={runStep} />}

        {phase === 'results' && analysis && posting && (
          <ResultsStep
            analysis={analysis}
            posting={posting}
            resumeName={selectedResume ? resumeLabel(selectedResume) : 'Your CV'}
            onRerun={restart}
            onViewSuggestions={() => setPhase('suggestions')}
          />
        )}

        {phase === 'suggestions' && analysis && (
          <SuggestionsStep
            analysis={analysis}
            counted={counted}
            onToggle={(index) =>
              setCounted((current) => {
                const next = new Set(current);
                if (!next.delete(index)) next.add(index);
                return next;
              })
            }
            onBack={() => setPhase('results')}
          />
        )}
      </main>
    </div>
  );
}
