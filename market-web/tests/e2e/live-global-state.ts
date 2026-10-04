import { execFileSync } from 'node:child_process';
import type { GlobalStateSnapshot } from '../../src/global-state';

const container = process.env.MARKET_DB_CONTAINER ?? 'docker-ace-db-1';
const shard = process.env.MARKET_E2E_SHARD_DATABASE ?? 'ace_market_e2e_shard';
const auth = process.env.MARKET_E2E_AUTH_DATABASE ?? 'ace_market_e2e_auth';
const settingKeys = ['market_active_listings', 'market_purchases_per_minute'];

function sql(database: string, statement: string): string[] {
  const output = execFileSync('docker', ['exec', '-i', container, 'sh', '-c', `MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --batch --skip-column-names ${database}`], { input: `${statement}\n`, encoding: 'utf8' });
  return output.trimEnd().split('\n').filter(Boolean);
}

function quote(value: string) { return `'${value.replaceAll("'", "''")}'`; }

export async function readGlobalState(accounts: readonly string[]): Promise<GlobalStateSnapshot> {
  const pause = sql(shard, "SELECT `value`, IFNULL(`description`, '') FROM config_properties_boolean WHERE `key` = 'market_paused'")[0]?.split('\t') ?? [];
  const settings = Object.fromEntries(settingKeys.map(key => {
    const row = sql(shard, `SELECT \`value\`, IFNULL(\`description\`, '') FROM config_properties_long WHERE \`key\` = ${quote(key)}`)[0];
    return [key, row ?? 'ABSENT'];
  }));
  const bannedDetails: Record<string, string> = {};
  const bannedAccounts: string[] = [];
  for (const account of accounts) {
    const row = sql(auth, `SELECT IFNULL(DATE_FORMAT(banned_Time, '%Y-%m-%d %H:%i:%s'), 'NULL'), IFNULL(CAST(banned_By_Account_Id AS CHAR), 'NULL'), IFNULL(DATE_FORMAT(ban_Expire_Time, '%Y-%m-%d %H:%i:%s'), 'NULL'), IFNULL(ban_Reason, 'NULL') FROM account WHERE accountName = ${quote(account)}`)[0] ?? 'NULL\tNULL\tNULL\tNULL';
    bannedDetails[account] = row;
    if (!row.startsWith('NULL\t')) bannedAccounts.push(account);
  }
  return { paused: pause[0] === '1', pausePresent: pause.length > 0, pauseReason: pause[1] ?? null, bannedAccounts, bannedDetails, settings };
}

export async function mutateGlobalState(accounts: readonly string[]) {
  const accountList = accounts.map(quote).join(',');
  sql(shard, "INSERT INTO config_properties_boolean (`key`, `value`, `description`) VALUES ('market_paused', 1, 'E2E forced pause') ON DUPLICATE KEY UPDATE `value` = 1, `description` = 'E2E forced pause'");
  sql(shard, "REPLACE INTO config_properties_long (`key`, `value`, `description`) VALUES ('market_active_listings', 1, 'E2E forced setting'), ('market_purchases_per_minute', 1, 'E2E forced setting')");
  sql(auth, `UPDATE account SET banned_Time = UTC_TIMESTAMP(), banned_By_Account_Id = 1, ban_Expire_Time = UTC_TIMESTAMP() + INTERVAL 1 DAY, ban_Reason = 'E2E forced ban' WHERE accountName IN (${accountList})`);
}

export async function restoreGlobalState(snapshot: GlobalStateSnapshot, accounts: readonly string[]) {
  const pauseValue = snapshot.paused ? 1 : 0;
  const pauseDescription = snapshot.pauseReason ?? '';
  if (snapshot.pausePresent === false) sql(shard, "DELETE FROM config_properties_boolean WHERE `key` = 'market_paused'");
  else sql(shard, `INSERT INTO config_properties_boolean (\`key\`, \`value\`, \`description\`) VALUES ('market_paused', ${pauseValue}, ${quote(pauseDescription)}) ON DUPLICATE KEY UPDATE \`value\` = ${pauseValue}, \`description\` = ${quote(pauseDescription)}`);
  for (const key of settingKeys) {
    const value = snapshot.settings[key] ?? 'ABSENT';
    if (value === 'ABSENT') sql(shard, `DELETE FROM config_properties_long WHERE \`key\` = ${quote(key)}`);
    else {
      const [number, description] = value.split('\t');
      sql(shard, `REPLACE INTO config_properties_long (\`key\`, \`value\`, \`description\`) VALUES (${quote(key)}, ${Number(number)}, ${quote(description ?? '')})`);
    }
  }
  for (const account of accounts) {
    const [banned, by, expires, reason] = (snapshot.bannedDetails?.[account] ?? 'NULL\tNULL\tNULL\tNULL').split('\t').map(value => value === 'NULL' ? null : value);
    const value = (part: string | null) => part === null ? 'NULL' : quote(part);
    sql(auth, `UPDATE account SET banned_Time = ${value(banned)}, banned_By_Account_Id = ${value(by)}, ban_Expire_Time = ${value(expires)}, ban_Reason = ${value(reason)} WHERE accountName = ${quote(account)}`);
  }
}
