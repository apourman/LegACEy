import type { Route } from './+types/listing';
import { ApiError, errorMessage, getListing } from '../api';
import { apiFor } from '../bff/api.server';
import { ListingPage, type ListingView } from '../ListingPage';

/**
 * A listing renders on the server with its item, price and appraisal, so a shared link opens complete. A sold, delisted, expired or missing
 * listing renders "No longer available" (with the API's 404 or 410).
 */
export async function loader({ request, context, params }: Route.LoaderArgs) {
  const api = await apiFor(request, context);
  const view: ListingView = { item: null, gone: false, error: '' };

  if (!/^\d{1,18}$/.test(params.id)) return api.respondClearingEndedSession({ ...view, gone: true }, { status: 404 });

  try {
    view.item = await getListing(Number(params.id), api.client);
  } catch (e) {
    if (e instanceof ApiError && (e.status === 404 || e.status === 410)) return api.respondClearingEndedSession({ ...view, gone: true }, { status: e.status });
    view.error = errorMessage(e, 'Could not load this listing.');
  }
  return api.respondClearingEndedSession(view);
}

export default function ListingRoute({ loaderData, params }: Route.ComponentProps) {
  // a new listing starts with its own page state (no purchase dialog left open)
  return <ListingPage key={params.id} view={loaderData} />;
}
