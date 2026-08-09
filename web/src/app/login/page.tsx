import { redirect } from 'next/navigation';

import { readSession } from '@/lib/session';

import { LoginForm } from './LoginForm';
import styles from './login.module.css';

export default async function LoginPage() {
  if (await readSession()) redirect('/analysis');

  return (
    <main className={styles.wrap}>
      <div className={`card ${styles.card}`}>
        <div className={styles.brand}>
          <span className={styles.mark} aria-hidden="true">
            B
          </span>
          <h1 className={styles.title}>Sign in to BuildCv</h1>
        </div>

        <LoginForm />

        <p className={styles.hint}>
          Accounts are created through <code>POST /v1/auth/register</code>. There is no sign-up screen
          yet — this build implements the analysis flow only.
        </p>
      </div>
    </main>
  );
}
