'use client';

import { ArrowLeft, Plus, Trash, Warning, Zap } from '@/components/icons';
import type { RequirementPriority } from '@/lib/contracts';

import styles from './analysis.module.css';
import { Notice } from './Notice';

export interface DraftRequirement {
  key: string;
  skill: string;
  priority: RequirementPriority;
  /** True when the API defaulted this priority rather than reading it out of the offer text. */
  guessed: boolean;
}

interface RequirementsStepProps {
  title: string;
  companyName: string;
  requirements: DraftRequirement[];
  fieldErrors: Record<string, string[]>;
  error: string | null;
  busy: boolean;
  onTitleChange: (value: string) => void;
  onCompanyChange: (value: string) => void;
  onRequirementChange: (key: string, patch: Partial<DraftRequirement>) => void;
  onRemoveRequirement: (key: string) => void;
  onAddRequirement: () => void;
  onBack: () => void;
  onRun: () => void;
}

function fieldError(errors: Record<string, string[]>, path: string): string | undefined {
  return errors[path]?.[0];
}

/**
 * The step the source design does not have, and cannot be skipped.
 *
 * `POST /v1/scoring/score` takes a job posting ID, not text. The only route from pasted text to a
 * posting that carries skill requirements — and therefore to a nonzero `weights.skills` on the
 * analysis — runs through `/job-offers/extract` and then `/job-offers/import`, and the proposal in
 * between is explicitly marked: `priorityGuessed` says the API defaulted a priority rather than
 * reading it. Posting the proposal straight back would accept those guesses on the candidate's
 * behalf and bake them into a score they are then told is deterministic.
 *
 * Title and company are asked for here because `/job-offers/import` REQUIRES both and there is
 * nothing in the extraction that supplies them.
 */
export function RequirementsStep({
  title,
  companyName,
  requirements,
  fieldErrors,
  error,
  busy,
  onTitleChange,
  onCompanyChange,
  onRequirementChange,
  onRemoveRequirement,
  onAddRequirement,
  onBack,
  onRun,
}: RequirementsStepProps) {
  const guessedCount = requirements.filter((r) => r.guessed).length;
  const canRun = title.trim() !== '' && companyName.trim() !== '' && !busy;

  return (
    <div className={`${styles.screen} ${styles.narrow}`}>
      <button type="button" className="btn btnGhost" style={{ padding: 0 }} onClick={onBack}>
        <ArrowLeft size={13} /> Back to inputs
      </button>

      <h1 className={styles.pageTitle} style={{ marginTop: 8 }}>
        Confirm what the posting asks for
      </h1>
      <p className={styles.pageLead} style={{ marginBottom: 20 }}>
        These were read out of the text you pasted. Correct them before scoring — they are the
        requirements the skills part of your score is measured against.
      </p>

      {error && <Notice variant="error">{error}</Notice>}

      {guessedCount > 0 && (
        <Notice variant="warn">
          {guessedCount === 1
            ? 'One priority below was defaulted rather than read from the text.'
            : `${guessedCount} priorities below were defaulted rather than read from the text.`}{' '}
          A must-have counts for more than a nice-to-have, so a wrong one moves the score.
        </Notice>
      )}

      {requirements.length === 0 && (
        <Notice>
          No skills were recognised in that text. You can add them by hand, or score without any — the
          skills section will then carry a weight of 0 and the rest of the score is renormalized to
          still total 100.
        </Notice>
      )}

      <div className={`card ${styles.panel}`} style={{ marginBottom: 16 }}>
        <div className={styles.fieldRow}>
          <div className={styles.field}>
            <label className={styles.fieldLabel} htmlFor="offer-title">
              Job title
            </label>
            <input
              id="offer-title"
              className={`${styles.input} ${fieldError(fieldErrors, 'title') ? styles.inputInvalid : ''}`}
              value={title}
              onChange={(e) => onTitleChange(e.target.value)}
              placeholder="Staff Product Designer"
            />
            {fieldError(fieldErrors, 'title') && (
              <span className={styles.fieldError}>{fieldError(fieldErrors, 'title')}</span>
            )}
          </div>

          <div className={styles.field}>
            <label className={styles.fieldLabel} htmlFor="offer-company">
              Company
            </label>
            <input
              id="offer-company"
              className={`${styles.input} ${fieldError(fieldErrors, 'companyName') ? styles.inputInvalid : ''}`}
              value={companyName}
              onChange={(e) => onCompanyChange(e.target.value)}
              placeholder="Datawise"
            />
            {fieldError(fieldErrors, 'companyName') && (
              <span className={styles.fieldError}>{fieldError(fieldErrors, 'companyName')}</span>
            )}
          </div>
        </div>
      </div>

      <div className={`card ${styles.panel}`}>
        <h2 className={styles.panelTitle}>Skill requirements</h2>
        <p className={styles.panelLead}>
          At most 100 are accepted. Each one is weighted by its priority.
        </p>

        <div className={styles.stack} style={{ gap: 12 }}>
          {requirements.map((requirement, index) => {
            const skillError = fieldError(fieldErrors, `requirements[${index}].skill`);
            const priorityError = fieldError(fieldErrors, `requirements[${index}].priority`);

            return (
              <div key={requirement.key}>
                <div className={styles.requirementRow}>
                  <div className={styles.field}>
                    <input
                      className={`${styles.input} ${skillError ? styles.inputInvalid : ''}`}
                      value={requirement.skill}
                      aria-label={`Skill ${index + 1}`}
                      onChange={(e) => onRequirementChange(requirement.key, { skill: e.target.value })}
                    />
                    {skillError && <span className={styles.fieldError}>{skillError}</span>}
                  </div>

                  <div className={styles.field}>
                    <select
                      className={`${styles.select} ${priorityError ? styles.inputInvalid : ''}`}
                      value={requirement.priority}
                      aria-label={`Priority for skill ${index + 1}`}
                      onChange={(e) =>
                        onRequirementChange(requirement.key, {
                          priority: e.target.value,
                          // Editing the priority settles it: the "guessed" marker describes what the
                          // extraction did, and it stops being true the moment a human chooses.
                          guessed: false,
                        })
                      }
                    >
                      <option value="MustHave">Must have</option>
                      <option value="NiceToHave">Nice to have</option>
                    </select>
                    {priorityError && <span className={styles.fieldError}>{priorityError}</span>}
                  </div>

                  <button
                    type="button"
                    className={styles.iconButton}
                    aria-label={`Remove ${requirement.skill || `skill ${index + 1}`}`}
                    onClick={() => onRemoveRequirement(requirement.key)}
                  >
                    <Trash size={14} />
                  </button>
                </div>

                {requirement.guessed && (
                  <span className={styles.guessedBadge} style={{ marginTop: 6 }}>
                    <Warning size={11} /> Priority defaulted
                  </span>
                )}
              </div>
            );
          })}
        </div>

        <button
          type="button"
          className="btn btnGhost"
          style={{ marginTop: 12 }}
          onClick={onAddRequirement}
        >
          <Plus size={13} /> Add a requirement
        </button>
      </div>

      <button
        type="button"
        className="btn btnPrimary btnLarge"
        style={{ marginTop: 16 }}
        disabled={!canRun}
        onClick={onRun}
      >
        <Zap size={15} />
        {busy ? 'Scoring…' : 'Run analysis'}
      </button>

      {/*
        A disabled button with no explanation is a dead end. Nothing in the extraction supplies a
        title or a company, so this is the normal state on arrival rather than an error the candidate
        caused, and it says which field is still empty instead of just refusing.
      */}
      {!canRun && !busy && (
        <p className={styles.footnote}>
          {title.trim() === '' && companyName.trim() === ''
            ? 'Add the job title and company to continue — the import requires both.'
            : title.trim() === ''
              ? 'Add the job title to continue.'
              : 'Add the company to continue.'}
        </p>
      )}
    </div>
  );
}
