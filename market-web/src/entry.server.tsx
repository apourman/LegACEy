import { PassThrough } from 'node:stream';
import { createReadableStreamFromReadable } from '@react-router/node';
import { renderToPipeableStream } from 'react-dom/server';
import { ServerRouter, type EntryContext } from 'react-router';

// Exported for server.js through the server build (build.entry.module), so the server needs nothing from the app's sources
export { createBffHandler } from './bff/handler.server';
export { bffSettings, loadContext } from './bff/context.server';

export const streamTimeout = 5_000;

/** Renders the whole page before answering (no streaming), so the HTML that arrives already holds the data */
export default function handleRequest(request: Request, status: number, headers: Headers, routerContext: EntryContext) {
  return new Promise<Response>((resolve, reject) => {
    let ready = false;
    const { pipe, abort } = renderToPipeableStream(<ServerRouter context={routerContext} url={request.url} />, {
      onAllReady() {
        ready = true;
        const body = new PassThrough();
        headers.set('Content-Type', 'text/html; charset=utf-8');
        resolve(new Response(createReadableStreamFromReadable(body), { status, headers }));
        pipe(body);
      },
      onShellError(error: unknown) { reject(error); },
      onError(error: unknown) {
        status = 500;
        if (ready) console.error(error);
      },
    });
    setTimeout(abort, streamTimeout + 1_000);
  });
}
