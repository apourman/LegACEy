import type { Route } from './+types/browse';
import { browse, getFacets, type BrowseResult, type Facets } from '../api';
import { apiFor } from '../bff/api.server';
import { Browse } from '../Browse';

/**
 * Browse renders on the server: the first page of listings for the address's filters and sort, and the facets, through the API with the
 * visitor's session if any. "Load more" fetches further pages in the browser. A cursor in a shared address is ignored: it belongs to one page.
 */
export async function loader({ request, context }: Route.LoaderArgs) {
  const filters = new URL(request.url).searchParams;
  filters.delete('cursor');

  const api = await apiFor(request, context);
  const [listings, facets] = await Promise.allSettled([browse(filters, api.client), getFacets(api.client)]);
  const failure = [listings, facets].find(outcome => outcome.status === 'rejected');

  return api.respond({
    result: listings.status === 'fulfilled' ? listings.value : { listings: [], nextCursor: null } as BrowseResult,
    facets: facets.status === 'fulfilled' ? facets.value : null as Facets | null,
    error: failure ? (failure.reason instanceof Error ? failure.reason.message : 'Could not load listings.') : '',
  });
}

export default function BrowseRoute({ loaderData }: Route.ComponentProps) {
  return <Browse first={loaderData.result} facets={loaderData.facets} loadError={loaderData.error} />;
}
