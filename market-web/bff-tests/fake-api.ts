import { createServer, type IncomingHttpHeaders, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';

// A fake Market API: an in-process HTTP server that records every request and answers from a script. The BFF request tests (harness.ts) and
// the browser checks (tests/website.spec.ts) both point the BFF at one.

export interface RecordedRequest { method: string; path: string; search: string; headers: IncomingHttpHeaders; body: string }
export interface ScriptedAnswer { status?: number; json?: unknown; body?: string; headers?: Record<string, string | string[]> }
/** An answer, or 'abort' to drop the connection without one */
export type Script = (request: RecordedRequest) => ScriptedAnswer | 'abort' | Promise<ScriptedAnswer | 'abort'>;

export interface FakeApi {
  url: string;
  requests: RecordedRequest[];
  /** Answers method + path (exact, before the query) with the script; later scripts win. Unscripted requests get 404 not_found. */
  on(method: string, path: string, script: Script | ScriptedAnswer): void;
  reset(): void;
  close(): Promise<void>;
}

/** Starts the fake on 127.0.0.1, on the given port or any free one */
export async function startFakeApi(port = 0): Promise<FakeApi> {
  let scripts: { method: string; path: string; script: Script }[] = [];
  const requests: RecordedRequest[] = [];

  const server: Server = createServer(async (incoming, outgoing) => {
    const chunks: Buffer[] = [];
    for await (const chunk of incoming) chunks.push(chunk as Buffer);
    const url = new URL(incoming.url ?? '/', 'http://fake-api');
    const request: RecordedRequest = { method: incoming.method ?? '', path: url.pathname, search: url.search, headers: incoming.headers, body: Buffer.concat(chunks).toString('utf8') };
    requests.push(request);

    const match = [...scripts].reverse().find(s => s.method === request.method && s.path === request.path);
    const answer = match ? await match.script(request) : { status: 404, json: { error: 'not_found' } };
    if (answer === 'abort') { outgoing.socket?.destroy(); return; }

    const headers: Record<string, string | string[]> = { ...(answer.json !== undefined ? { 'Content-Type': 'application/json; charset=utf-8' } : {}), ...answer.headers };
    outgoing.writeHead(answer.status ?? 200, headers);
    outgoing.end(answer.json !== undefined ? JSON.stringify(answer.json) : answer.body ?? '');
  });
  await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(port, '127.0.0.1', resolve); });

  return {
    url: `http://127.0.0.1:${(server.address() as AddressInfo).port}`,
    requests,
    on(method, path, script) { scripts.push({ method, path, script: typeof script === 'function' ? script : () => script }); },
    reset() { scripts = []; requests.length = 0; },
    close: () => new Promise(resolve => { server.closeAllConnections(); server.close(() => resolve()); }),
  };
}
