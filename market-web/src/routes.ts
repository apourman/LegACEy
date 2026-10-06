import { index, route, type RouteConfig } from '@react-router/dev/routes';

export default [
  // pages
  index('routes/browse.tsx'),
  route('listing/:id', 'routes/listing.tsx'),
  route('signin', 'routes/signin.tsx'),
  route('vault', 'routes/vault.tsx'),
  route('history', 'routes/history.tsx'),
  // the BFF's own routes, answering JSON or text: sign-in and sign-out, the /api/* proxy, and health
  route('auth/sign-in', 'routes/auth.sign-in.ts'),
  route('auth/sign-out', 'routes/auth.sign-out.ts'),
  route('api/*', 'routes/api.ts'),
  route('health', 'routes/health.ts'),
  route('*', 'routes/not-found.tsx'),
] satisfies RouteConfig;
