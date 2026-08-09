import type { NextConfig } from 'next';

// No rewrites to the API on purpose.
//
// A rewrite would put the browser's own request on the wire to BuildCv.Api, which drags along
// everything the BFF exists to avoid: CORS with credentials, the double-submit CSRF header, and the
// antiforgery token that has to be re-fetched on every change of principal — including access-token
// expiry, where an idle client's next POST answers 403 instead of 401 (documented in CLAUDE.md).
//
// Every call to the API goes through a route handler under src/app/api instead, server-side, with an
// `Authorization: Bearer` header. CsrfGuardMiddleware skips bearer requests by design, so none of
// that machinery applies and the browser never holds a BuildCv credential.
const nextConfig: NextConfig = {
  reactStrictMode: true,
  poweredByHeader: false,
};

export default nextConfig;
