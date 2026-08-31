import { test as setup, expect } from "@playwright/test";
import path from "path";

// Storage state file for reuse across tests
export const STORAGE_STATE = path.join(__dirname, "storageState.json");

// Admin credentials — set during installation wizard
const ADMIN_EMAIL = "admin@smartstore.com";
const ADMIN_PASSWORD = process.env.ADMIN_PASSWORD || "Admin1234!";

// Inject a cookie to bypass the Smartstore cookie-consent modal.
// The modal only appears when the consent cookie is absent; setting it via
// browser context avoids the full JS / Bootstrap modal animation cycle.
async function acceptCookies(page: import("@playwright/test").Page) {
  await page.context().addCookies([
    {
      name: "CookieConsent",
      value: "true",
      domain: "localhost",
      path: "/",
    },
  ]);
}

setup("authenticate as admin", async ({ page }) => {
  // Pre-accept cookies so the consent modal does not block clicks.
  await acceptCookies(page);

  // Navigate to the login page
  await page.goto("/login/");
  await page.waitForLoadState("networkidle");

  // Force-hide the cookie modal if it still appears (belt-and-suspenders).
  await page.evaluate(() => {
    const modal = document.getElementById("cookie-manager-window");
    if (modal) {
      modal.style.display = "none";
      modal.remove();
    }
    // Also remove any modal-backdrop overlay
    document.querySelectorAll(".modal-backdrop").forEach((el) => el.remove());
    document.body.classList.remove("modal-open");
  });

  // Verify we are on the login page with the form visible
  await expect(page.locator('input[name="UsernameOrEmail"]')).toBeVisible({
    timeout: 15000,
  });

  // Fill in the login form (selectors confirmed by browser inspection)
  await page.fill('input[name="UsernameOrEmail"]', ADMIN_EMAIL);
  await page.fill('input[name="Password"]', ADMIN_PASSWORD);

  // Click the Log in button (confirmed class btn-login, unique to this form)
  await page.locator("button.btn-login").click({ force: true });

  // Wait for successful login and redirect
  await page.waitForLoadState("networkidle");

  // After login, navigate to admin area
  await page.goto("/admin/");
  await page.waitForLoadState("networkidle");

  // Confirm we are on the admin panel
  await expect(page).toHaveURL(/\/admin\//);

  // Save storage state so other tests can reuse the session
  await page.context().storageState({ path: STORAGE_STATE });
});
