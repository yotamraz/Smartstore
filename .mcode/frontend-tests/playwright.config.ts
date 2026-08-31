import { defineConfig } from "@playwright/test";
import path from "path";

// Storage state captured from the QA agent's authenticated browser session
const STORAGE_STATE = path.join(__dirname, "storageState.json");

// MCODE_DIR: use env var if set, otherwise derive from this file's location
const MCODE_DIR = process.env.MCODE_DIR || path.join(__dirname, "..");

export default defineConfig({
  testDir: ".",
  timeout: 60000,
  retries: 1,
  use: {
    baseURL: "http://localhost:5000",
    headless: true,
    screenshot: "on",
    video: "retain-on-failure",
    trace: "retain-on-failure",
  },
  projects: [
    {
      name: "setup",
      testMatch: "**/auth.setup.ts",
    },
    {
      name: "chromium",
      testMatch: "**/*.spec.ts",
      dependencies: ["setup"],
      use: {
        storageState: STORAGE_STATE,
      },
    },
  ],
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
