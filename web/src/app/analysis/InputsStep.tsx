'use client';

import { Check, Zap } from '@/components/icons';
import type { ResumeResponse } from '@/lib/contracts';
import { plural, relativeTime, resumeLabel } from '@/lib/format';

import styles from './analysis.module.css';
import { Notice } from './Notice';

/**
 * Below this, `POST /v1/job-offers/extract` has too little text to propose anything useful. It is a
 * hint in the UI only — the server decides what it can extract, and an empty proposal is a valid
 * answer this screen handles rather than prevents.
 */
const USEFUL_JD_LENGTH = 80;

interface InputsStepProps {
  resumes: ResumeResponse[] | null;
  resumesError: string | null;
  selectedResumeId: string | null;
  onSelectResume: (id: string) => void;
  jobDescription: string;
  onJobDescriptionChange: (value: string) => void;
  busy: boolean;
  onExtract: () => void;
}

export function InputsStep({
  resumes,
  resumesError,
  selectedResumeId,
  onSelectResume,
  jobDescription,
  onJobDescriptionChange,
  busy,
  onExtract,
}: InputsStepProps) {
  const enoughText = jobDescription.trim().length > USEFUL_JD_LENGTH;
  const canContinue = enoughText && selectedResumeId !== null && !busy;

  return (
    <div className={styles.screen}>
      <div style={{ marginBottom: 28 }}>
        <h1 className={styles.pageTitle}>Run an ATS analysis</h1>
        <p className={styles.pageLead}>
          Pick a CV, paste the job description, and get a deterministic match score.
        </p>
      </div>

      <div className={styles.inputsGrid}>
        <div>
          <div className={styles.sectionLabel} id="resume-label">
            1. Choose a CV
          </div>

          {resumesError && <Notice variant="error">{resumesError}</Notice>}

          {resumes === null && !resumesError && (
            <p className={styles.pageLead}>Loading your CVs…</p>
          )}

          {resumes?.length === 0 && (
            <div className={`card ${styles.empty}`}>
              You have no CVs yet. Create one through the API — <code>POST /v1/resumes</code> or{' '}
              <code>POST /v1/resumes/import</code> — and it will appear here.
            </div>
          )}

          {resumes && resumes.length > 0 && (
            <div className={styles.resumeList} role="radiogroup" aria-labelledby="resume-label">
              {resumes.map((resume) => {
                const selected = resume.id === selectedResumeId;

                return (
                  <button
                    key={resume.id}
                    type="button"
                    role="radio"
                    aria-checked={selected}
                    onClick={() => onSelectResume(resume.id)}
                    className={`${styles.resumeOption} ${selected ? styles.resumeOptionSelected : ''}`}
                  >
                    <span
                      className={`${styles.radio} ${selected ? styles.radioSelected : ''}`}
                      aria-hidden="true"
                    >
                      {selected && <span className={styles.radioDot} />}
                    </span>
                    <span className={styles.resumeBody}>
                      <span className={styles.resumeName}>{resumeLabel(resume)}</span>
                      {/*
                        No score badge. The source design showed one per CV, and there is nothing on
                        the wire to fill it: a match score needs a job posting, and the readability
                        score comes from a POST that creates a report — not a field on a list row.
                      */}
                      <span className={styles.resumeMeta}>
                        Edited {relativeTime(resume.updatedAt)} ·{' '}
                        {plural(resume.skills.length, 'skill')} ·{' '}
                        {plural(resume.experiences.length, 'role')}
                      </span>
                    </span>
                  </button>
                );
              })}
            </div>
          )}
        </div>

        <div>
          <div className={styles.sectionLabel}>
            <label htmlFor="job-description">2. Paste the job description</label>
          </div>

          {/*
            One input, no tab strip. The source design offered "Paste text" beside "Upload file";
            there is no endpoint that accepts a job posting as a file, so the second tab would have
            been a control that cannot do anything.
          */}
          <div className={styles.jdBox}>
            <textarea
              id="job-description"
              rows={12}
              className={styles.jdTextarea}
              placeholder="Paste the full job description here — title, responsibilities, requirements…"
              value={jobDescription}
              onChange={(e) => onJobDescriptionChange(e.target.value)}
            />
            <div className={styles.jdFooter}>
              <span>{jobDescription.length} characters</span>
              {enoughText && (
                <span className={styles.jdReady}>
                  <Check size={12} /> Long enough to extract from
                </span>
              )}
            </div>
          </div>

          <button
            type="button"
            className="btn btnPrimary btnLarge"
            style={{ marginTop: 16 }}
            disabled={!canContinue}
            onClick={onExtract}
          >
            <Zap size={15} />
            {busy ? 'Reading the posting…' : 'Extract requirements'}
          </button>

          <p className={styles.footnote}>
            Scoring is deterministic — the same CV against the same posting always produces the same
            score.
          </p>
        </div>
      </div>
    </div>
  );
}
