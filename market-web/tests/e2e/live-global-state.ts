import { execFileSync } from "node:child_process";

const container = process.env.MARKET_DB_CONTAINER ?? "docker-ace-db-1";
const shard = process.env.MARKET_E2E_SHARD_DATABASE ?? "ace_market_e2e_shard";
const auth = process.env.MARKET_E2E_AUTH_DATABASE ?? "ace_market_e2e_auth";
const settingKeys = ["market_active_listings", "market_purchases_per_minute"];

interface ConfigRow {
    value: number;
    description: string | null;
}

interface Ban {
    bannedAt: string | null;
    bannedByAccountId: number | null;
    expiresAt: string | null;
    reason: string | null;
}

/** What the global-state tests change: the pause row, two market settings and the test accounts' bans. A null row is a missing one. */
export interface GlobalState {
    pause: ConfigRow | null;
    settings: Record<string, ConfigRow | null>;
    bans: Record<string, Ban>;
}

export type GlobalStateConcern = "pause" | "settings" | "bans";

const notBanned: Ban = {
    bannedAt: null,
    bannedByAccountId: null,
    expiresAt: null,
    reason: null,
};

/** Runs a statement as root in the e2e MySQL container; returns the first column of the first row, if any (raw, so JSON stays intact). */
function runMysql(database: string, statement: string): string | null {
    const output = execFileSync(
        "docker",
        [
            "exec",
            "-i",
            container,
            "sh",
            "-c",
            `MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --batch --raw --skip-column-names ${database}`,
        ],
        { input: `${statement}\n`, encoding: "utf8" },
    ).trimEnd();
    return output === "" || output === "NULL" ? null : output.split("\n")[0];
}

function readJson<T>(database: string, select: string): T | null {
    const row = runMysql(database, select);
    return row === null ? null : (JSON.parse(row) as T);
}

function quote(value: string | number | null) {
    if (value === null) return "NULL";
    if (typeof value === "number") return String(value);
    return `'${value.replaceAll("'", "''")}'`;
}

function writePause(row: ConfigRow | null) {
    runMysql(
        shard,
        row === null
            ? "DELETE FROM config_properties_boolean WHERE `key` = 'market_paused'"
            : `REPLACE INTO config_properties_boolean (\`key\`, \`value\`, \`description\`) VALUES ('market_paused', ${row.value}, ${quote(row.description)})`,
    );
}

function writeSetting(key: string, row: ConfigRow | null) {
    runMysql(
        shard,
        row === null
            ? `DELETE FROM config_properties_long WHERE \`key\` = ${quote(key)}`
            : `REPLACE INTO config_properties_long (\`key\`, \`value\`, \`description\`) VALUES (${quote(key)}, ${row.value}, ${quote(row.description)})`,
    );
}

function writeBan(account: string, ban: Ban) {
    runMysql(
        auth,
        `UPDATE account SET banned_Time = ${quote(ban.bannedAt)}, banned_By_Account_Id = ${quote(ban.bannedByAccountId)}, ban_Expire_Time = ${quote(ban.expiresAt)}, ban_Reason = ${quote(ban.reason)} WHERE accountName = ${quote(account)}`,
    );
}

export function readGlobalState(accounts: readonly string[]): GlobalState {
    const pause = readJson<ConfigRow>(
        shard,
        "SELECT JSON_OBJECT('value', `value`, 'description', `description`) FROM config_properties_boolean WHERE `key` = 'market_paused'",
    );
    const settings =
        readJson<Record<string, ConfigRow>>(
            shard,
            `SELECT JSON_OBJECTAGG(\`key\`, JSON_OBJECT('value', \`value\`, 'description', \`description\`)) FROM config_properties_long WHERE \`key\` IN (${settingKeys.map(quote).join(",")})`,
        ) ?? {};
    const time = (column: string) =>
        `DATE_FORMAT(${column}, '%Y-%m-%d %H:%i:%s')`;
    const bans =
        readJson<Record<string, Ban>>(
            auth,
            `SELECT JSON_OBJECTAGG(accountName, JSON_OBJECT('bannedAt', ${time("banned_Time")}, 'bannedByAccountId', banned_By_Account_Id, 'expiresAt', ${time("ban_Expire_Time")}, 'reason', ban_Reason)) FROM account WHERE accountName IN (${accounts.map(quote).join(",")})`,
        ) ?? {};
    return {
        pause,
        settings: Object.fromEntries(
            settingKeys.map((key) => [key, settings[key] ?? null]),
        ),
        bans: Object.fromEntries(
            accounts.map((account) => [account, bans[account] ?? notBanned]),
        ),
    };
}

export function mutateGlobalState(
    accounts: readonly string[],
    concerns: readonly GlobalStateConcern[],
) {
    if (concerns.includes("pause"))
        writePause({ value: 1, description: "E2E forced pause" });
    if (concerns.includes("settings"))
        for (const key of settingKeys)
            writeSetting(key, { value: 1, description: "E2E forced setting" });
    if (concerns.includes("bans"))
        for (const account of accounts)
            writeBan(account, {
                bannedAt: "2000-01-01 00:00:00",
                bannedByAccountId: 1,
                expiresAt: "2999-01-01 00:00:00",
                reason: "E2E forced ban",
            });
}

function restoreGlobalState(state: GlobalState) {
    writePause(state.pause);
    for (const [key, row] of Object.entries(state.settings))
        writeSetting(key, row);
    for (const [account, ban] of Object.entries(state.bans))
        writeBan(account, ban);
}

/** Runs a test step that changes global state, then puts back what was there before it, even when the step fails. */
export async function withRestoredGlobalState<T>(
    accounts: readonly string[],
    action: () => Promise<T>,
): Promise<T> {
    const before = readGlobalState(accounts);
    try {
        return await action();
    } finally {
        restoreGlobalState(before);
    }
}
