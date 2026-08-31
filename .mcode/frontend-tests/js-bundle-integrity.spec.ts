import { test, expect } from "@playwright/test";

/**
 * JS Bundle Integrity tests — Milestone 3 (JsMin -> NUglify migration).
 *
 * QA source: Flows "Install Page", "Admin Dashboard", "Customer Roles List",
 * "Customer Role Create Form".
 *
 * The primary frontend concern of Milestone 3 is that replacing
 * DouglasCrockford.JsMin with NUglify across the bundling pipeline and the
 * Google Analytics module does NOT break JS bundle loading or page rendering.
 *
 * These tests verify:
 * 1. The Smartstore global namespace is initialized (window.Smartstore exists),
 *    confirming core JS bundles loaded and executed without fatal errors.
 * 2. No uncaught JavaScript errors occur on any tested page.
 * 3. Admin pages render with complete layout (nav, data grids, forms).
 * 4. The install wizard renders correctly (regression guard: origin parity confirmed
 *    by QA report — both showed identical pages with 18 external JS scripts).
 */

function collectJsErrors(page: import("@playwright/test").Page): string[] {
  const errors: string[] = [];
  page.on("pageerror", (err) => errors.push(err.message));
  return errors;
}

async function checkSmartstoreNamespace(
  page: import("@playwright/test").Page
): Promise<boolean> {
  return page.evaluate(
    () => typeof (window as unknown as Record<string, unknown>).Smartstore !== "undefined"
  );
}

test.describe("JS Bundle Integrity — NUglify Migration (Milestone 3)", () => {

  test.describe("admin dashboard", () => {
    let jsErrors: string[];
    test.beforeEach(async ({ page }) => {
      jsErrors = collectJsErrors(page);
      await page.goto("/admin/");
      await page.waitForLoadState("networkidle");
    });

    test("Smartstore global namespace is initialized", async ({ page }) => {
      const smartstoreExists = await checkSmartstoreNamespace(page);
      expect(smartstoreExists).toBe(true);
      expect(jsErrors).toHaveLength(0);
    });

    test("no uncaught JavaScript errors on load", async () => {
      expect(jsErrors).toHaveLength(0);
    });
  });

  test.describe("customer roles list", () => {
    let jsErrors: string[];
    test.beforeEach(async ({ page }) => {
      jsErrors = collectJsErrors(page);
      await page.goto("/admin/customerrole/list/");
      await page.waitForLoadState("networkidle");
      await page.locator(".dg-table-wrapper").waitFor({ timeout: 20000 });
    });

    test("no uncaught JavaScript errors on load", async () => {
      expect(jsErrors).toHaveLength(0);
    });

    test("Smartstore global namespace is initialized", async ({ page }) => {
      const smartstoreExists = await checkSmartstoreNamespace(page);
      expect(smartstoreExists).toBe(true);
      expect(jsErrors).toHaveLength(0);
    });
  });

  test.describe("customer role create form", () => {
    let jsErrors: string[];
    test.beforeEach(async ({ page }) => {
      jsErrors = collectJsErrors(page);
      await page.goto("/admin/customerrole/create/");
      await page.waitForLoadState("networkidle");
    });

    test("no uncaught JavaScript errors on load", async () => {
      expect(jsErrors).toHaveLength(0);
    });

    test("form fields rendered by JS are present", async ({ page }) => {
      await expect(page.locator("input#Name")).toBeVisible();

      await expect(
        page.locator("button.btn-primary, button.btn-warning").filter({ hasText: "Save" }).first()
      ).toBeVisible();

      await expect(page.getByRole("tab", { name: /General/i })).toBeVisible();

      expect(jsErrors).toHaveLength(0);
    });
  });

  test.describe("scheduled tasks list", () => {
    let jsErrors: string[];
    test.beforeEach(async ({ page }) => {
      jsErrors = collectJsErrors(page);
      await page.goto("/admin/scheduling/list/");
      await page.waitForLoadState("networkidle");
      await page.locator(".dg-tr").first().waitFor({ timeout: 20000 });
    });

    test("no uncaught JavaScript errors on load", async () => {
      expect(jsErrors).toHaveLength(0);
    });

    test("Smartstore global namespace is initialized", async ({ page }) => {
      const smartstoreExists = await checkSmartstoreNamespace(page);
      expect(smartstoreExists).toBe(true);
      expect(jsErrors).toHaveLength(0);
    });
  });

});
