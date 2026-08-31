import { test, expect } from "@playwright/test";

/**
 * Scheduled Tasks tests — verifying TargetGroupEvaluatorTask presence.
 *
 * QA source: Flows "Scheduled Tasks Page" and "TargetGroupEvaluatorTask Edit Page".
 * Auth is provided via storageState.json (no manual login needed).
 *
 * The milestone task appears at /admin/scheduling/list/ with:
 *   - Name: "Update assignments of customers to customer roles"
 *   - CRON: "15 2 * * *"
 *   - Enabled: Yes (checkbox input, checked=true)
 * Edit page confirmed at /admin/scheduling/edit/10/.
 * Grid rows use .dg-tr class (confirmed via browser inspection).
 */

test.describe("Scheduled Tasks List", () => {
  test("should render the scheduled tasks list page", async ({ page }) => {
    await page.goto("/admin/scheduling/list/");
    await page.waitForLoadState("networkidle");

    // Grid rows use .dg-tr class (confirmed via browser inspection)
    const rows = page.locator(".dg-tr");
    await expect(rows.first()).toBeVisible({ timeout: 20000 });
  });

  test("should show TargetGroupEvaluatorTask in the task list", async ({
    page,
  }) => {
    await page.goto("/admin/scheduling/list/");
    await page.waitForLoadState("networkidle");

    // QA report: milestone task name appears as a .dg-cell a link
    await expect(
      page.locator(".dg-cell a", {
        hasText: "Update assignments of customers to customer roles",
      })
    ).toBeVisible({ timeout: 20000 });
  });

  test("should show CRON expression 15 2 * * * for TargetGroupEvaluatorTask", async ({
    page,
  }) => {
    await page.goto("/admin/scheduling/list/");
    await page.waitForLoadState("networkidle");

    // Wait for grid to fully populate with milestone task
    await expect(
      page.locator(".dg-cell a", {
        hasText: "Update assignments of customers to customer roles",
      })
    ).toBeVisible({ timeout: 20000 });

    // QA report: The row shows "15 2 * * *" as CRON expression in the grid
    await expect(
      page.locator(".dg-cell", { hasText: "15 2 * * *" })
    ).toBeVisible({ timeout: 10000 });
  });

  test("should have at least 3 scheduled tasks in the grid", async ({
    page,
  }) => {
    await page.goto("/admin/scheduling/list/");
    await page.waitForLoadState("networkidle");

    // QA report listed 5 visible tasks; assert at least 3 are present
    // Grid rows use .dg-tr (confirmed via browser inspection)
    const rows = page.locator(".dg-tr");
    await expect(rows.first()).toBeVisible({ timeout: 20000 });
    const count = await rows.count();
    expect(count).toBeGreaterThanOrEqual(3);
  });
});

test.describe("TargetGroupEvaluatorTask Edit Page", () => {
  test("should render the task edit page with correct name", async ({
    page,
  }) => {
    // QA report: task edit page is at /admin/scheduling/edit/10/
    await page.goto("/admin/scheduling/edit/10/");
    await page.waitForLoadState("networkidle");

    // QA report: input[name="Name"] has value
    // "Update assignments of customers to customer roles"
    const nameInput = page.locator('input[name="Name"]');
    await expect(nameInput).toBeVisible();
    await expect(nameInput).toHaveValue(
      "Update assignments of customers to customer roles"
    );
  });

  test("should show CRON expression 15 2 * * * on the task edit page", async ({
    page,
  }) => {
    await page.goto("/admin/scheduling/edit/10/");
    await page.waitForLoadState("networkidle");

    // QA report: input[name="CronExpression"] has value "15 2 * * *"
    // This is the exact CRON spec from the milestone
    const cronInput = page.locator('input[name="CronExpression"]');
    await expect(cronInput).toBeVisible();
    await expect(cronInput).toHaveValue("15 2 * * *");
  });

  test("should show Enabled toggle as ON for TargetGroupEvaluatorTask", async ({
    page,
  }) => {
    await page.goto("/admin/scheduling/edit/10/");
    await page.waitForLoadState("networkidle");

    // QA report: Enabled toggle is ON (checked=true)
    // Verified via browser: input#Enabled, type=checkbox, checked=true
    // Milestone spec compliance: Enabled=true CONFIRMED
    const enabledInput = page.locator("input#Enabled");
    await expect(enabledInput).toBeVisible();
    // Use JavaScript to verify checked state since toggle styling may interfere
    const isChecked = await enabledInput.evaluate(
      (el) => (el as HTMLInputElement).checked
    );
    expect(isChecked).toBe(true);
  });

  test("should show Run now link on the task edit page", async ({ page }) => {
    await page.goto("/admin/scheduling/edit/10/");
    await page.waitForLoadState("networkidle");

    // Browser inspection: "Run now" is an anchor <a> element (not a button)
    // linking to /admin/scheduling/runjob/10/
    await expect(page.locator("a", { hasText: "Run now" })).toBeVisible();
  });

  test("should show Save button on the task edit page", async ({ page }) => {
    await page.goto("/admin/scheduling/edit/10/");
    await page.waitForLoadState("networkidle");

    // Browser inspection: Save button uses btn-warning class
    await expect(
      page.locator("button.btn-warning", { hasText: "Save" })
    ).toBeVisible();
  });
});
