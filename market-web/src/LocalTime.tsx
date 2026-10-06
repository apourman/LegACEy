import { useSyncExternalStore } from 'react';

const subscribe = () => () => {};

/**
 * A time in the reader's own time zone and format. The server doesn't know them, so the server's render (and the browser's first render, which
 * must match it) shows UTC; the browser switches to local time straight after.
 */
export function LocalTime({ iso, dateOnly = false }: { iso: string; dateOnly?: boolean }) {
  const local = useSyncExternalStore(subscribe, () => true, () => false);
  const value = new Date(iso);
  const text = local
    ? (dateOnly ? value.toLocaleDateString() : value.toLocaleString())
    : (dateOnly ? value.toISOString().slice(0, 10) : value.toISOString().slice(0, 16).replace('T', ' ') + ' UTC');
  return <time dateTime={iso}>{text}</time>;
}
