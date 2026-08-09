'use client';

import { ArrowLeft, ArrowRight, Check, Zap } from '@/components/icons';
import type { AnalysisResponse } from '@/lib/contracts';
import { bandTone, impactPoints, priorityLabel, priorityTone, sectionLabel } from '@/lib/format';

import styles from './analysis.module.css';
import { Notice } from './Notice';

interface SuggestionsStepProps {
  analysis: AnalysisResponse;
  counted: ReadonlySet<number>;
  onToggle: (index: number) => void;
  onBack: () => void;
}

export function SuggestionsStep({ analysis, counted, onToggle, onBack }: SuggestionsStepProps) {
  const gained = analysis.recommendations.reduce(
    (total, recommendation, index) => (counted.has(index) ? total + recommendation.impact * 100 : total),
    0,
  );

  const projected = Math.min(100, Math.round(analysis.overallScore + gained));
  const projectedTone = bandTone(
    projected >= 80 ? 'Strong' : projected >= 60 ? 'Good' : projected >= 40 ? 'Medium' : 'Low',
  );

  return (
    <div className={`${styles.screen} ${styles.narrow}`}>
      <div className={styles.resultsHead}>
        <div>
          <button type="button" className="btn btnGhost" style={{ padding: 0 }} onClick={onBack}>
            <ArrowLeft size={13} /> Back to results
          </button>
          <h1 className={styles.pageTitle} style={{ marginTop: 8 }}>
            Suggestions
          </h1>
        </div>

        <div className={styles.projection}>
          <div style={{ textAlign: 'center' }}>
            <div className={styles.projectionValue} style={{ color: 'var(--primary)' }}>
              {analysis.overallScore}
            </div>
            <div className={styles.projectionCaption}>CURRENT</div>
          </div>
          <ArrowRight size={16} />
          <div style={{ textAlign: 'center' }}>
            <div className={styles.projectionValue} style={{ color: projectedTone.fg }}>
              {projected}
            </div>
            <div className={styles.projectionCaption}>PROJECTED</div>
          </div>
        </div>
      </div>

      {/*
        Two things the source design showed that do not exist, stated instead of faked.

        There is no endpoint that edits a CV from a recommendation, so "Apply to resume" would have
        been a button that changed nothing; the toggle below only moves the projection. And the
        before/after diff had to go entirely — the API returns a sentence and a measured impact, and
        no rewritten text to put on either side of a diff.
      */}
      <Notice>
        Each impact is measured on its own: the exact increase in the score from closing that one gap,
        re-evaluated through the same formula. They are <strong>not guaranteed to add up</strong> when
        several are acted on together, so the projection is a guide rather than a promise. Editing
        happens on the CV itself — nothing here changes it.
      </Notice>

      <div className={styles.stack} style={{ gap: 12 }}>
        {analysis.recommendations.length === 0 && (
          <div className={`card ${styles.empty}`}>
            This run produced no recommendations. Advice a candidate cannot act on is not emitted, so
            an empty list here is a complete answer.
          </div>
        )}

        {analysis.recommendations.map((recommendation, index) => {
          const selected = counted.has(index);
          const tone = priorityTone(recommendation.priority);

          return (
            <div
              key={`${recommendation.kind}-${index}`}
              className={`card ${styles.suggestionCard} ${selected ? styles.suggestionSelected : ''}`}
            >
              <div className={styles.suggestionLayout}>
                <span className={styles.suggestionIcon} aria-hidden="true">
                  <Zap size={15} />
                </span>

                <div style={{ flex: 1, minWidth: 0 }}>
                  <div className={styles.suggestionHead}>
                    <span
                      className={styles.tag}
                      style={{ color: 'var(--good)', background: 'var(--good-bg)' }}
                    >
                      {impactPoints(recommendation.impact)}
                    </span>
                    <span
                      className={styles.tag}
                      style={{ color: tone.fg, background: tone.bg }}
                    >
                      {priorityLabel(recommendation.priority)}
                    </span>
                    <span
                      className={styles.tag}
                      style={{ color: 'var(--fg-muted)', background: 'var(--border-soft)' }}
                    >
                      {sectionLabel(recommendation.section)}
                    </span>
                  </div>

                  <p className={styles.suggestionMessage}>{recommendation.message}</p>

                  <button
                    type="button"
                    className={selected ? 'btn' : 'btn btnPrimary'}
                    style={{ height: 32, fontSize: 12.5 }}
                    aria-pressed={selected}
                    onClick={() => onToggle(index)}
                  >
                    {selected ? (
                      <>
                        <Check size={13} /> Counted
                      </>
                    ) : (
                      'Count in projection'
                    )}
                  </button>
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
