import { defineConfig } from "@playwright/test";
import path from "path";

// Storage state captured from the QA agent's authenticated browser session
const STORAGE_STATE = path.join(__dirname, "storageState.json");

// MCODE_DIR: use env var if set, otherwise fall back to the known sandbox path
const MCODE_DIR =
  process.env.MCODE_DIR ||
  "C:/Users/yotam/.local/share/modelcode/workspace/jobs/98d40b29-8828-41de-8c99-f03f9ad0594f/mcode";

export default defineConfig({
  testDir: ".",
  timeout: 60000,
  retries: 1,
  use: {
    baseURL: "http://localhost:5000",
    headless: true,
    storageState: STORAGE_STATE,
    screenshot: "on",
    video: "retain-on-failure",
    trace: "retain-on-failure",
  },
  reporter: [
    ["list"],
    [
      "json",
      {
        outputFile: `${MCODE_DIR}/fe_testing/playwright-results.json`,
      },
    ],
  ],
});
