'use client';

import { Check } from '@/components/icons';

import styles from './analysis.module.css';
import { ScoreRing } from './ScoreRing';

/**
 * The two requests that actually happen, and nothing else.
 *
 * The source design animated four named stages — "Parse resume structure", "Extract keywords",
 * "Match skills", "Compute score" — off a timer that ticked 2% every 70ms while a single request was
 * in flight. Those stages are real work inside the scoring engine, but nothing streams their
 * progress, so the bar was measuring a `setInterval` and not the server.
 *
 * These two steps are the two round trips this screen makes. The ring moves when one finishes,
 * because that is the only moment anything is actually known.
 */
const STEPS = [
  { label: 'Create the job offer', detail: 'POST /v1/job-offers/import' },
  { label: 'Score the CV against it', detail: 'POST /v1/scoring/score' },
] as const;

export function RunningStep({ step }: { step: number }) {
  const percent = Math.round((step / STEPS.length) * 100);

  return (
    <div className={`${styles.screen} ${styles.progressWrap}`} aria-live="polite">
      <ScoreRing
        value={percent}
        size={120}
        thickness={8}
        color="var(--primary)"
        track="var(--border)"
        label={`${percent}%`}
      />

      <h1 className={styles.pageTitle} style={{ margin: '28px 0 6px' }}>
        {STEPS[Math.min(step, STEPS.length - 1)]?.label ?? 'Working…'}
      </h1>
      <p className={styles.pageLead} style={{ marginBottom: 32 }}>
        Scoring runs in one request. This usually takes a moment.
      </p>

      <div className={styles.stepList}>
        {STEPS.map((entry, index) => {
          const done = index < step;
          const active = index === step;

          return (
            <div
              key={entry.label}
              className={`${styles.stepRow} ${!done && !active ? styles.stepPending : ''}`}
            >
              <span
                className={`${styles.stepBullet} ${done ? styles.stepDone : ''}`}
                aria-hidden="true"
              >
                {done && <Check size={11} />}
                {active && <span className={styles.spinner} />}
                {!done && !active && <span className={styles.stepDot} />}
              </span>
              <span>{entry.label}</span>
              <span style={{ marginLeft: 'auto', fontSize: 11.5, color: 'var(--fg-faint)' }}>
                {entry.detail}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}
