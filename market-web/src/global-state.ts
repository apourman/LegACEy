export interface GlobalSettingSnapshot {
    value: number;
    description: string;
}

export interface BanSnapshot {
    bannedAt: string | null;
    bannedByAccountId: string | null;
    expiresAt: string | null;
    reason: string | null;
}

/** State changed by a serial end-to-end journey. Restore is always attempted, including a failed test. */
export interface GlobalStateSnapshot {
    paused: boolean;
    pausePresent?: boolean;
    pauseReason?: string | null;
    bannedDetails: Readonly<Record<string, BanSnapshot>>;
    settings: Readonly<Record<string, GlobalSettingSnapshot | null>>;
}

export async function withRestoredGlobalState<T>(
    snapshot: GlobalStateSnapshot,
    action: () => Promise<T>,
    restore: (state: GlobalStateSnapshot) => Promise<void>,
): Promise<T> {
    try {
        return await action();
    } finally {
        await restore({
            paused: snapshot.paused,
            pausePresent: snapshot.pausePresent,
            pauseReason: snapshot.pauseReason ?? null,
            bannedDetails: Object.fromEntries(
                Object.entries(snapshot.bannedDetails).map(([account, ban]) => [
                    account,
                    { ...ban },
                ]),
            ),
            settings: Object.fromEntries(
                Object.entries(snapshot.settings).map(([key, setting]) => [
                    key,
                    setting ? { ...setting } : null,
                ]),
            ),
        });
    }
}
