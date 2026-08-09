'use client';

import { ArrowRight, Check, Cross, Warning } from '@/components/icons';
import type { AnalysisResponse, JobPostingResponse, SectionScoreResponse } from '@/lib/contracts';
import { bandCopy, bandTone, missingSkills, sectionLabel, toPercent } from '@/lib/format';

import styles from './analysis.module.css';
import { Notice } from './Notice';
import { ScoreRing } from './ScoreRing';

/** A section is a result only when it carried weight. Below this it is treated as unmeasured. */
const MEASURED = 0;

/** What counts as a section worth calling out. Chosen here, and labelled as this app's threshold. */
const STRONG_SECTION = 0.8;

interface ResultsStepProps {
  analysis: AnalysisResponse;
  posting: JobPostingResponse;
  resumeName: string;
  onRerun: () => void;
  onViewSuggestions: () => void;
}

function orderSections(sections: SectionScoreResponse[]): SectionScoreResponse[] {
  return [...sections].sort((a, b) => {
    const measured = Number(b.weight > MEASURED) - Number(a.weight > MEASURED);
    return measured !== 0 ? measured : b.weight - a.weight;
  });
}

export function ResultsStep({
  analysis,
  posting,
  resumeName,
  onRerun,
  onViewSuggestions,
}: ResultsStepProps) {
  const tone = bandTone(analysis.band);
  const sections = orderSections(analysis.breakdown.sections);
  const measured = sections.filter((s) => s.weight > MEASURED);
  const unmeasured = sections.filter((s) => s.weight <= MEASURED);
  const strong = measured.filter((s) => s.score >= STRONG_SECTION);
  const isMissing = missingSkills(analysis.recommendations);
  const missingCount = posting.requirements.filter((r) => isMissing(r.skill)).length;

  return (
    <div className={styles.screen}>
      <div className={styles.resultsHead}>
        <div>
          <div className={styles.eyebrow}>
            {resumeName} · vs · {posting.title}
            {posting.companyName ? ` at ${posting.companyName}` : ''}
          </div>
          <h1 className={styles.pageTitle}>Analysis results</h1>
        </div>
        <div style={{ display: 'flex', gap: 8 }}>
          <button type="button" className="btn" onClick={onRerun}>
            New analysis
          </button>
          <button type="button" className="btn btnPrimary" onClick={onViewSuggestions}>
            View suggestions <ArrowRight size={13} />
          </button>
        </div>
      </div>

      {/*
        Only rendered when true. `POST /v1/scoring/score` always answers false — the run it returns
        was either just computed or reused precisely because the CV had not moved — so this is here
        for the day this screen reads a stored analysis back, not as decoration.
      */}
      {analysis.isStale && (
        <Notice variant="warn">
          This CV has been edited since the score was taken, so the number below describes a version
          you no longer have. Run the analysis again to refresh it.
        </Notice>
      )}

      <div className={styles.resultsGrid}>
        <div className={`card ${styles.scoreCard}`}>
          <ScoreRing
            value={analysis.overallScore}
            size={150}
            thickness={11}
            color={tone.fg}
            track="var(--border-soft)"
            label={String(analysis.overallScore)}
            caption="MATCH SCORE"
          />

          <div className={styles.bandPill} style={{ color: tone.fg, background: tone.bg }}>
            {bandCopy(analysis.band)}
          </div>

          <p className={styles.scoreNote}>
            A fact about this CV <em>against this posting</em> — not a rating of the CV on its own,
            and never to be added to a readability score. Scoring model v
            {analysis.breakdown.weights.schemaVersion}; two analyses on different versions are not
            comparable.
          </p>
        </div>

        <div className={styles.stack}>
          <div className={`card ${styles.panel}`}>
            <h2 className={styles.panelTitle}>Score breakdown</h2>
            <p className={styles.panelLead}>
              Weights are renormalized to total 100% across the sections this posting actually asked
              about.
            </p>

            <div className={styles.barList}>
              {measured.map((section) => {
                const score = toPercent(section.score);
                const sectionTone = bandTone(
                  score >= 80 ? 'Strong' : score >= 60 ? 'Good' : score >= 40 ? 'Medium' : 'Low',
                );

                return (
                  <div key={section.section}>
                    <div className={styles.barHead}>
                      <span>{sectionLabel(section.section)}</span>
                      <span className={styles.barValue} style={{ color: sectionTone.fg }}>
                        {score}{' '}
                        <span className={styles.barUnit}>
                          / 100 · weight {toPercent(section.weight)}%
                        </span>
                      </span>
                    </div>
                    <div className={styles.barTrack}>
                      <div
                        className={styles.barFill}
                        style={{ width: `${score}%`, background: sectionTone.fg }}
                      />
                    </div>
                  </div>
                );
              })}

              {/*
                Sections with weight 0 are listed WITHOUT their score, and that is the contract rather
                than a design choice: the API states the score beside a zero weight measures nothing,
                so rendering it as a result — a red "0 / 100" — would be reporting a failure the
                posting never asked about.
              */}
              {unmeasured.map((section) => (
                <div key={section.section} className={styles.barHead}>
                  <span className={styles.unmeasured}>{sectionLabel(section.section)}</span>
                  <span className={styles.barUnit}>
                    not measured · this posting expressed no weighted requirement
                  </span>
                </div>
              ))}
            </div>
          </div>

          <div className={styles.duoGrid}>
            <div className={`card ${styles.panel}`}>
              <h2 className={styles.panelTitle} style={{ color: 'var(--good)', fontSize: 14 }}>
                Strengths
              </h2>
              <p className={styles.panelLead}>Sections that carried weight and scored 80 or above.</p>
              <div className={styles.stack} style={{ gap: 10 }}>
                {strong.length === 0 && (
                  <span className={styles.unmeasured}>No section reached 80 on this posting.</span>
                )}
                {strong.map((section) => (
                  <div key={section.section} className={styles.listRow}>
                    <Check size={14} />
                    <span>
                      {sectionLabel(section.section)} — {toPercent(section.score)} / 100
                    </span>
                  </div>
                ))}
              </div>
            </div>

            <div className={`card ${styles.panel}`}>
              <h2 className={styles.panelTitle} style={{ color: 'var(--warn)', fontSize: 14 }}>
                Gaps
              </h2>
              <p className={styles.panelLead}>
                The highest-impact advice this run produced. Every one is on the next screen.
              </p>
              <div className={styles.stack} style={{ gap: 10 }}>
                {analysis.recommendations.length === 0 && (
                  <span className={styles.unmeasured}>
                    Nothing to act on — this run produced no recommendations.
                  </span>
                )}
                {analysis.recommendations.slice(0, 3).map((recommendation, index) => (
                  <div key={`${recommendation.kind}-${index}`} className={styles.listRow}>
                    <Warning size={14} />
                    <span>{recommendation.message}</span>
                  </div>
                ))}
              </div>
            </div>
          </div>
        </div>
      </div>

      <div className={`card ${styles.panel}`}>
        <h2 className={styles.panelTitle}>Skills this posting asked for</h2>
        <p className={styles.panelLead}>
          {posting.requirements.length === 0
            ? 'None were recorded, so the skills section carried no weight in the score above.'
            : `${missingCount} of ${posting.requirements.length} flagged as missing from your CV.`}
        </p>

        <div className={styles.chipRow}>
          {posting.requirements.map((requirement) => {
            const missing = isMissing(requirement.skill);

            return (
              <span
                key={requirement.skill}
                className={styles.chip}
                style={
                  missing
                    ? {
                        background: 'var(--bad-bg)',
                        color: 'var(--bad-fg)',
                        borderColor: 'var(--bad-border)',
                      }
                    : undefined
                }
              >
                {missing && <Cross size={11} />}
                {requirement.skill}
                <span className={styles.chipMuted}>
                  {requirement.priority === 'MustHave' ? 'must have' : 'nice to have'}
                </span>
              </span>
            );
          })}
        </div>

        {/*
          No green "found" chip anywhere, and this is the reason. The API caps recommendations at ten
          and returns the highest-impact ones, so a skill with no flag beside it may simply have been
          ranked out — absence of advice is not evidence of a match. Re-deriving the match here from
          the CV's own skill list would be worse: the engine recognises alternative spellings
          ("React.js" satisfies "React"), so a second matcher written in this file would eventually
          contradict the score printed above it.
        */}
        {posting.requirements.length > 0 && (
          <p className={styles.footnote} style={{ textAlign: 'left', marginTop: 12 }}>
            Only the ten highest-impact recommendations are returned, so a skill without a flag has
            not necessarily been matched.
          </p>
        )}
      </div>
    </div>
  );
}
