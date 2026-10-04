import { execFileSync } from "node:child_process";
import { homedir } from "node:os";
import { join } from "node:path";

export default function globalSetup() {
    const root =
        process.env.MARKET_DEV_ROOT ??
        process.cwd().replace(/\/market-web$/, "");
    const runDirectory =
        process.env.MARKET_E2E_RUN_DIR ??
        join(
            process.env.XDG_STATE_HOME ?? join(homedir(), ".local", "state"),
            "legacey",
            "market-e2e",
        );
    const config =
        process.env.MARKET_DEV_CONFIG ?? join(runDirectory, "Config.js");
    for (const character of [
        "journeys desktop Alpha",
        "journeys phone Alpha",
    ]) {
        execFileSync(
            `${root}/scripts/market/dev.sh`,
            ["fixture", "--character", character],
            {
                cwd: root,
                env: {
                    ...process.env,
                    MARKET_DEV_ROOT: root,
                    MARKET_DEV_CONFIG: config,
                },
                stdio: "inherit",
            },
        );
    }
}
