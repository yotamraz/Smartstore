import { test, expect } from "@playwright/test";

/**
 * Customer Roles tests.
 *
 * QA source: Flows "Customer Roles List" and "Customer Role Edit".
 * Auth is provided via storageState.json (no manual login needed).
 *
 * Grid structure: .dg-tr (row elements within .dg-table-wrapper).
 * 4 roles confirmed by QA: Administrators, Forum Moderators, Guests, Registered.
 * Edit page:
 *   - Registered role: /admin/customerrole/edit/3/
 *   - Guests role:     /admin/customerrole/edit/4/
 * The Customers tab has #SelectedRuleSetIds — the rule-set assignment UI that
 * wires TargetGroupEvaluatorTask automated customer assignments to this role.
 */

test.describe("Customer Roles List", () => {
  test("should render the customer roles list page", async ({ page }) => {
    await page.goto("/admin/customerrole/list/");
    // Grid loads asynchronously via XHR (QA report)
    await page.waitForLoadState("networkidle");

    // Verify the grid container is present
    await expect(page.locator(".dg-table-wrapper")).toBeVisible({
      timeout: 20000,
    });
  });

  test("should show at least one role row in the grid", async ({ page }) => {
    await page.goto("/admin/customerrole/list/");
    await page.waitForLoadState("networkidle");

    // Grid rows use .dg-tr class (confirmed via browser inspection)
    // QA report confirmed 4 roles; assert at least 1 row rendered
    const rows = page.locator(".dg-tr");
    await expect(rows.first()).toBeVisible({ timeout: 20000 });
    const count = await rows.count();
    expect(count).toBeGreaterThanOrEqual(1);
  });

  test("should show the Registered role in the grid", async ({ page }) => {
    await page.goto("/admin/customerrole/list/");
    await page.waitForLoadState("networkidle");

    // QA report: role name links are .dg-cell a elements
    // Registered role links to /admin/customerrole/edit/3/
    await expect(
      page.locator(".dg-cell a", { hasText: "Registered" })
    ).toBeVisible({ timeout: 20000 });
  });

  test("should show an add new customer role button", async ({ page }) => {
    await page.goto("/admin/customerrole/list/");
    await page.waitForLoadState("networkidle");

    // Browser inspection: "Add new…" button links to /admin/customerrole/create/
    await expect(
      page.locator("a.btn", { hasText: "Add new" })
    ).toBeVisible({ timeout: 15000 });
  });
});

test.describe("Customer Role Edit — General Tab", () => {
  test("should open the Registered role edit page", async ({ page }) => {
    // Registered role is at /admin/customerrole/edit/3/ (confirmed via grid links)
    await page.goto("/admin/customerrole/edit/3/");
    await page.waitForLoadState("networkidle");

    // input#Name holds the role name value. It resolves in DOM but is visually
    // hidden by CSS (toggle/switch styling). Use toHaveValue to assert value
    // without requiring visual visibility.
    const nameInput = page.locator("input#Name");
    await expect(nameInput).toHaveValue("Registered");
  });

  test("should show the Active toggle on the General tab", async ({ page }) => {
    await page.goto("/admin/customerrole/edit/3/");
    await page.waitForLoadState("networkidle");

    // input#Active is a checkbox hidden by CSS toggle styling.
    // The input is checked=true; verify via JS evaluation.
    const isChecked = await page
      .locator("input#Active")
      .evaluate((el) => (el as HTMLInputElement).checked);
    expect(isChecked).toBe(true);
  });

  test("should show Save button on the edit page", async ({ page }) => {
    await page.goto("/admin/customerrole/edit/3/");
    await page.waitForLoadState("networkidle");

    // Browser inspection confirmed Save button uses btn-warning class
    await expect(
      page.locator("button.btn-warning", { hasText: "Save" })
    ).toBeVisible();
  });
});

test.describe("Customer Role Edit — Customers Tab (TargetGroupEvaluatorTask UI)", () => {
  test("should show the Customers tab on the role edit page", async ({
    page,
  }) => {
    await page.goto("/admin/customerrole/edit/3/");
    await page.waitForLoadState("networkidle");

    // Click the Customers tab
    await page.getByRole("tab", { name: "Customers" }).click();
    await page.waitForLoadState("networkidle");

    // QA report: #SelectedRuleSetIds rule set selector is visible on this tab
    await expect(page.locator("#SelectedRuleSetIds")).toBeVisible({
      timeout: 15000,
    });
  });

  test("should have TargetGroupEvaluatorTask rule set assignment UI", async ({
    page,
  }) => {
    await page.goto("/admin/customerrole/edit/3/");
    await page.waitForLoadState("networkidle");

    await page.getByRole("tab", { name: "Customers" }).click();
    await page.waitForLoadState("networkidle");

    // QA report: #SelectedRuleSetIds with data-select-url pointing to
    // /admin/rule/allrulesets/?scope=Customer confirms TargetGroupEvaluatorTask
    // rule set assignment wiring is present
    const ruleSetSelect = page.locator("#SelectedRuleSetIds");
    await expect(ruleSetSelect).toBeVisible({ timeout: 15000 });

    const dataSelectUrl = await ruleSetSelect.getAttribute("data-select-url");
    expect(dataSelectUrl).toBeTruthy();
    expect(dataSelectUrl).toContain("/admin/rule/allrulesets/");
    expect(dataSelectUrl).toContain("scope=Customer");
  });
});
