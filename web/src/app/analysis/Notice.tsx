import { Info, Warning } from '@/components/icons';

import styles from './analysis.module.css';

const VARIANTS = {
  info: styles.noticeInfo,
  warn: styles.noticeWarn,
  error: styles.noticeError,
} as const;

export function Notice({
  variant = 'info',
  children,
}: {
  variant?: keyof typeof VARIANTS;
  children: React.ReactNode;
}) {
  return (
    <div
      className={`${styles.notice} ${VARIANTS[variant]}`}
      role={variant === 'error' ? 'alert' : undefined}
    >
      {variant === 'info' ? <Info size={15} /> : <Warning size={15} />}
      <div>{children}</div>
    </div>
  );
}
