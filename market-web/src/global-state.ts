/** State changed by a serial end-to-end journey. Restore is always attempted, including a failed test. */
export interface GlobalStateSnapshot {
  paused: boolean;
  pauseReason?: string | null;
  bannedAccounts: readonly string[];
  bannedDetails?: Readonly<Record<string, string>>;
  settings: Readonly<Record<string, string>>;
}

export async function withRestoredGlobalState<T>(snapshot: GlobalStateSnapshot, action: () => Promise<T>, restore: (state: GlobalStateSnapshot) => Promise<void>): Promise<T> {
  try {
    return await action();
  } finally {
    await restore({ paused: snapshot.paused, pauseReason: snapshot.pauseReason ?? null, bannedAccounts: [...snapshot.bannedAccounts], bannedDetails: { ...snapshot.bannedDetails }, settings: { ...snapshot.settings } });
  }
}
