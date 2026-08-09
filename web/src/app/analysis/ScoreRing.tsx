import styles from './analysis.module.css';

interface ScoreRingProps {
  /** 0..100. */
  value: number;
  size: number;
  thickness: number;
  color: string;
  track: string;
  label: string;
  caption?: string;
}

/**
 * The dial from the source design, with the dash arithmetic derived rather than hard-coded.
 *
 * The original carried `stroke-dasharray="402.1"` as a literal — the circumference of one particular
 * radius — so changing the size silently produced a ring that filled to the wrong fraction. Here the
 * circumference comes from the radius that is actually drawn, and the two cannot disagree.
 */
export function ScoreRing({
  value,
  size,
  thickness,
  color,
  track,
  label,
  caption,
}: ScoreRingProps) {
  const radius = (size - thickness) / 2;
  const circumference = 2 * Math.PI * radius;
  const clamped = Math.min(100, Math.max(0, value));
  const offset = circumference * (1 - clamped / 100);

  return (
    <div className={styles.ringWrap} style={{ width: size, height: size }}>
      <svg
        width={size}
        height={size}
        viewBox={`0 0 ${size} ${size}`}
        style={{ transform: 'rotate(-90deg)' }}
        aria-hidden="true"
        focusable="false"
      >
        <circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke={track}
          strokeWidth={thickness}
        />
        <circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke={color}
          strokeWidth={thickness}
          strokeLinecap="round"
          strokeDasharray={circumference.toFixed(2)}
          strokeDashoffset={offset.toFixed(2)}
          style={{ transition: 'stroke-dashoffset 1s cubic-bezier(.4,0,.2,1)' }}
        />
      </svg>
      <div className={styles.ringCenter}>
        <span className={styles.ringValue} style={{ color, fontSize: size < 130 ? 24 : 40 }}>
          {label}
        </span>
        {caption && <span className={styles.ringCaption}>{caption}</span>}
      </div>
    </div>
  );
}
