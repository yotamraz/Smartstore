import { test as setup, expect } from "@playwright/test";
import path from "path";
import fs from "fs";

// Storage state file for reuse across tests
export const STORAGE_STATE = path.join(__dirname, "storageState.json");

// Admin credentials (from QA report)
const ADMIN_EMAIL = "admin@yourstore.com";
// Password sourced from lifecycle env_vars — fall back to common dev default
const ADMIN_PASSWORD = process.env.ADMIN_PASSWORD || "admin";

setup("authenticate as admin", async ({ page }) => {
  // Navigate to the admin area — redirects to login if not authenticated
  await page.goto("/admin/");

  // Fill in the login form (selectors confirmed by QA agent)
  await page.fill('input[name="Email"]', ADMIN_EMAIL);
  await page.fill('input[name="Password"]', ADMIN_PASSWORD);
  await page.click('button[type="submit"]');

  // Wait for redirect to admin dashboard
  await page.waitForURL("**/admin/**", { timeout: 15000 });
  await page.waitForLoadState("networkidle");

  // Confirm we are on the admin panel
  await expect(page).toHaveURL(/\/admin\//);

  // Save storage state so other tests can reuse the session
  await page.context().storageState({ path: STORAGE_STATE });
});
