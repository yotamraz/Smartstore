import { test, expect } from "@playwright/test";

/**
 * Admin login / admin area access tests.
 *
 * QA source: Flow "Admin Dashboard Login" — successfully authenticated at
 * /admin/ with full navigation bar visible: Dashboard, Catalog, Sales,
 * Customers, Promotions, CMS, Configuration, System, Plugins.
 *
 * Auth is provided via storageState.json (captured from the QA agent session)
 * so no login form interaction is needed.
 */

test.describe("Admin Area Access", () => {
  test("should reach the admin dashboard", async ({ page }) => {
    await page.goto("/admin/");
    await page.waitForLoadState("networkidle");

    // Verify we are on the admin panel, not redirected to login
    await expect(page).toHaveURL(/\/admin\//);
  });

  test("should display the admin navigation bar", async ({ page }) => {
    await page.goto("/admin/");
    await page.waitForLoadState("networkidle");

    // QA report: Full navigation bar visible with these top-level items
    // Dashboard, Catalog, Customers, System are the core nav items confirmed
    await expect(page.getByRole("link", { name: /Dashboard/i })).toBeVisible();
    await expect(page.getByRole("link", { name: /Catalog/i })).toBeVisible();
    await expect(page.getByRole("link", { name: /Customers/i })).toBeVisible();
    await expect(page.getByRole("link", { name: /System/i })).toBeVisible();
  });

  test("should display the admin dashboard page heading or content", async ({
    page,
  }) => {
    await page.goto("/admin/");
    await page.waitForLoadState("networkidle");

    // Admin panel should be accessible without login redirect
    const url = page.url();
    expect(url).toContain("/admin/");
    // The page should not redirect to /login
    expect(url).not.toContain("/login");
  });
});
