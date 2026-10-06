import { createRequestHandler, type AppLoadContext, type ServerBuild } from 'react-router';
import { crossSiteRefusal } from './csrf.server';

/**
 * The BFF's request handler: React Router's, behind the cross-site check, so a non-GET request to any path (one with no action included) is refused
 * 403 csrf unless it comes from the site itself. The actions and the /api/* proxy make the same check themselves, and the proxy adds the
 * X-Market-Request rule; this is the outer wall, not the only one.
 */
export function createBffHandler(build: ServerBuild, mode?: string) {
  const handle = createRequestHandler(build, mode);
  return (request: Request, context: AppLoadContext) => crossSiteRefusal(request, context.bff) ?? handle(request, context);
}
