import { test as setup, expect } from "@playwright/test";
import path from "path";

// Storage state file for reuse across tests
export const STORAGE_STATE = path.join(__dirname, "storageState.json");

// Admin credentials (from QA report — confirmed working)
const ADMIN_EMAIL = process.env.ADMIN_EMAIL || "admin@smartstore.com";
// Password sourced from lifecycle env_vars — fall back to common dev default
const ADMIN_PASSWORD = process.env.ADMIN_PASSWORD || "admin";

setup("authenticate as admin", async ({ page }) => {
  // Navigate to the storefront login page.
  // Smartstore redirects unauthenticated /admin/ requests to the storefront
  // /login page, so we log in through the customer login form.
  await page.goto("/login");
  await page.waitForLoadState("domcontentloaded");

  // Dismiss cookie consent modal if present.
  // On a fresh browser context (no cookies), Smartstore renders a Bootstrap
  // modal with id="cookie-manager-window" loaded via AJAX. The "Accept all"
  // button has id="accept-all". Wait briefly so the AJAX load has time to fire.
  const acceptAllBtn = page.locator("#accept-all");
  try {
    await acceptAllBtn.waitFor({ state: "visible", timeout: 8000 });
    await acceptAllBtn.click();
    // Wait for the modal to disappear (Bootstrap fade transition ~300ms)
    await page.locator("#cookie-manager-window").waitFor({ state: "hidden", timeout: 5000 });
  } catch {
    // Modal not present — cookies already accepted or consent not required
  }

  // Fill in the login form.
  // DOM-confirmed input name: "UsernameOrEmail" (not "Email") — verified via
  // agent-browser eval on the live /login page.
  await page.fill('input[name="UsernameOrEmail"]', ADMIN_EMAIL);
  await page.fill('input[name="Password"]', ADMIN_PASSWORD);

  // Submit the login form. The "Log in" button is inside the customer login
  // form which posts to /login. Use role+name to select exactly this button
  // (there are 7 type="submit" buttons on the page — search bar, newsletter,
  // theme switcher, cookie form, etc.).
  await page.getByRole("button", { name: "Log in" }).click();

  // Wait for the post-login redirect to settle
  await page.waitForLoadState("networkidle", { timeout: 20000 });

  // Now navigate to the admin area
  await page.goto("/admin/");
  await page.waitForLoadState("networkidle", { timeout: 30000 });

  // Confirm we reached the admin panel (not redirected back to /login)
  await expect(page).toHaveURL(/\/admin\//, { timeout: 10000 });

  // Persist the authenticated session for all downstream tests
  await page.context().storageState({ path: STORAGE_STATE });
});
