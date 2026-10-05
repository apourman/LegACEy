/** GET /health: the BFF is up. A bare "ok" that says nothing else, for container health checks and the smoke check; it doesn't call the API. */
export function loader() {
  return new Response('ok', { headers: { 'Content-Type': 'text/plain', 'Cache-Control': 'no-store' } });
}
