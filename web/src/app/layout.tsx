import type { Metadata } from 'next';
import { Inter } from 'next/font/google';

import './globals.css';

// Self-hosted by next/font at build time. The source design pulled Inter from fonts.googleapis.com
// on every page load, which is a third-party request on a page that renders a candidate's CV data.
const inter = Inter({ subsets: ['latin'], display: 'swap', variable: '--font-inter' });

export const metadata: Metadata = {
  title: 'BuildCv',
  description: 'Deterministic resume match and readability scoring.',
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" className={inter.variable}>
      <body>{children}</body>
    </html>
  );
}
