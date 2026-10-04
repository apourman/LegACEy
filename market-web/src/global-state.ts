/** State changed by a serial end-to-end journey. Restore is always attempted, including a failed test. */
export interface GlobalStateSnapshot {
  paused: boolean;
  bannedAccounts: readonly string[];
  settings: Readonly<Record<string, string>>;
}

export async function withRestoredGlobalState<T>(snapshot: GlobalStateSnapshot, action: () => Promise<T>, restore: (state: GlobalStateSnapshot) => Promise<void>): Promise<T> {
  try {
    return await action();
  } finally {
    await restore({ paused: snapshot.paused, bannedAccounts: [...snapshot.bannedAccounts], settings: { ...snapshot.settings } });
  }
}
