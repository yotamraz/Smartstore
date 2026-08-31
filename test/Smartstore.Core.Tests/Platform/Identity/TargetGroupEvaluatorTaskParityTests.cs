using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Smartstore.Core.Identity.Rules;
using Smartstore.Scheduling;

namespace Smartstore.Core.Tests.Platform.Identity;

/// <summary>
/// Tier 3 behavioral parity tests for <see cref="TargetGroupEvaluatorTask"/>.
/// These tests encode specific legacy behaviors as regression-preventing assertions.
/// </summary>
[TestFixture]
public class TargetGroupEvaluatorTaskParityTests : TargetGroupEvaluatorTaskTestBase
{
    /// <summary>
    /// Resolves the repository root directory by navigating from the test output directory.
    /// With AppendTargetFrameworkToOutputPath=false, output is bin/Debug/.
    /// From bin/Debug/ -> Smartstore.Core.Tests/ -> test/ -> Smartstore/ (repo root).
    /// </summary>
    private static string RepoRoot
    {
        get
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

            // Validate by checking for Smartstore.sln.
            if (!File.Exists(Path.Combine(repoRoot, "Smartstore.sln")))
            {
                throw new DirectoryNotFoundException(
                    $"Could not find Smartstore.sln at expected repo root: {repoRoot}. " +
                    $"AppContext.BaseDirectory = {AppContext.BaseDirectory}");
            }

            return repoRoot;
        }
    }

    /// <summary>
    /// Reads a source file relative to the repository root.
    /// </summary>
    private static string ReadSourceFile(string relativePath)
    {
        var fullPath = Path.Combine(RepoRoot, relativePath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Source file not found at: {fullPath}. Relative path: {relativePath}");
        }

        return File.ReadAllText(fullPath);
    }

    #region Test 1: Default CRON schedule

    [Test]
    public void Default_cron_schedule_is_0215_daily()
    {
        // Arrange: read the InvariantSeedData source file.
        var source = ReadSourceFile(
            Path.Combine("src", "Smartstore.Core", "Platform", "Installation", "SeedData", "InvariantSeedData.cs"));

        // Assert: the TargetGroupEvaluatorTask registration uses CRON expression "15 2 * * *".
        // This is a legacy behavioral parity assertion: if this value changes, the test fails.
        Assert.That(source, Does.Contain("nameof(TargetGroupEvaluatorTask)"),
            "InvariantSeedData must reference TargetGroupEvaluatorTask by name");

        Assert.That(source, Does.Contain(@"CronExpression = ""15 2 * * *"""),
            "TargetGroupEvaluatorTask must be scheduled at 02:15 daily (CRON: 15 2 * * *)");
    }

    #endregion

    #region Test 2: StopOnError is false

    [Test]
    public void StopOnError_is_false()
    {
        // Arrange: read the InvariantSeedData source file.
        var source = ReadSourceFile(
            Path.Combine("src", "Smartstore.Core", "Platform", "Installation", "SeedData", "InvariantSeedData.cs"));

        // We need to verify that the TargetGroupEvaluatorTask descriptor block has StopOnError = false.
        // Find the nameof(TargetGroupEvaluatorTask) reference, then search nearby lines for StopOnError.
        var taskTypeIndex = source.IndexOf("nameof(TargetGroupEvaluatorTask)", StringComparison.Ordinal);
        Assert.That(taskTypeIndex, Is.GreaterThan(-1),
            "InvariantSeedData must contain nameof(TargetGroupEvaluatorTask)");

        // Extract a window around the TargetGroupEvaluatorTask reference (500 chars before and after).
        // This window will contain the full TaskDescriptor initializer block.
        var windowStart = Math.Max(0, taskTypeIndex - 500);
        var windowEnd = Math.Min(source.Length, taskTypeIndex + 500);
        var window = source[windowStart..windowEnd];

        // Verify that StopOnError = false appears in this window, confirming
        // it is part of the same TaskDescriptor block as TargetGroupEvaluatorTask.
        Assert.That(window, Does.Contain("StopOnError = false"),
            "TargetGroupEvaluatorTask descriptor must have StopOnError = false");

        // Also verify the window does NOT contain StopOnError = true (to prevent accidental matches).
        Assert.That(window, Does.Not.Contain("StopOnError = true"),
            "TargetGroupEvaluatorTask descriptor must not have StopOnError = true");
    }

    #endregion

    #region Test 3: Legacy type name mapping in DbTaskStore

    [Test]
    public void Legacy_type_name_mapping_in_DbTaskStore()
    {
        // Arrange: access the private static readonly _legacyTypeNamesMap field via reflection.
        var dbTaskStoreType = typeof(DbTaskStore);
        var fieldInfo = dbTaskStoreType.GetField(
            "_legacyTypeNamesMap",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(fieldInfo, Is.Not.Null,
            "DbTaskStore must have a private static _legacyTypeNamesMap field");

        var legacyMap = fieldInfo.GetValue(null) as Dictionary<string, string>;
        Assert.That(legacyMap, Is.Not.Null,
            "_legacyTypeNamesMap must be a Dictionary<string, string>");

        // Assert: the map must contain the TargetGroupEvaluatorTask legacy mapping.
        var taskName = nameof(TargetGroupEvaluatorTask);
        Assert.That(legacyMap.ContainsKey(taskName), Is.True,
            $"_legacyTypeNamesMap must contain a key for '{taskName}'");

        Assert.That(legacyMap[taskName],
            Is.EqualTo("SmartStore.Services.Customers.TargetGroupEvaluatorTask, SmartStore.Services"),
            "Legacy type name for TargetGroupEvaluatorTask must map to the old SmartStore.Services assembly");
    }

    #endregion

    #region Test 4: Manual trigger passes CustomerRoleIds

    [Test]
    public void Manual_trigger_passes_CustomerRoleIds()
    {
        // Arrange: read the CustomerRoleController source file.
        var source = ReadSourceFile(
            Path.Combine("src", "Smartstore.Web", "Areas", "Admin", "Controllers", "CustomerRoleController.cs"));

        // Assert: the controller's ApplyRules action passes "CustomerRoleIds" as a parameter key.
        Assert.That(source, Does.Contain(@"""CustomerRoleIds"""),
            "CustomerRoleController must pass the literal string \"CustomerRoleIds\" as a task parameter key");

        // Also verify the TargetGroupEvaluatorTask source code uses the same parameter key.
        var taskSource = ReadSourceFile(
            Path.Combine("src", "Smartstore.Core", "Platform", "Identity", "Rules", "TargetGroupEvaluatorTask.cs"));

        Assert.That(taskSource, Does.Contain(@"""CustomerRoleIds"""),
            "TargetGroupEvaluatorTask must reference the literal string \"CustomerRoleIds\" as a parameter key");
    }

    #endregion

    #region Test 5: IsSystemMapping flag distinguishes auto from manual

    [Test]
    public void IsSystemMapping_flag_set_on_created_mappings_and_delete_scoped_to_system()
    {
        // Tier 3 parity assertion: verify via source-file reading that the production code
        // sets IsSystemMapping = true on new mappings and scopes deletion to system mappings.
        var source = ReadSourceFile(
            Path.Combine("src", "Smartstore.Core", "Platform", "Identity", "Rules", "TargetGroupEvaluatorTask.cs"));

        // New mappings are created with IsSystemMapping = true.
        Assert.That(source, Does.Contain("IsSystemMapping = true"),
            "TargetGroupEvaluatorTask must set IsSystemMapping = true on created mappings");

        // Delete query is scoped to system mappings only (manual mappings preserved).
        Assert.That(source, Does.Contain(".Where(x => x.IsSystemMapping)"),
            "Delete query must filter on IsSystemMapping to preserve manual mappings");
    }

    #endregion

    #region Test 6: Hook importance set to Important

    [Test]
    public void Hook_importance_set_to_Important()
    {
        // Arrange: read the TargetGroupEvaluatorTask source file.
        var source = ReadSourceFile(
            Path.Combine("src", "Smartstore.Core", "Platform", "Identity", "Rules", "TargetGroupEvaluatorTask.cs"));

        // Assert: the Run method creates a DbContextScope with minHookImportance: HookImportance.Important.
        // This is a legacy behavioral parity assertion: hooks below "Important" are suppressed during task execution.
        Assert.That(source, Does.Contain("HookImportance.Important"),
            "TargetGroupEvaluatorTask must use HookImportance.Important for the DbContextScope");

        // Verify the DbContextScope constructor call includes the minHookImportance parameter.
        Assert.That(source, Does.Contain("minHookImportance: HookImportance.Important"),
            "The minHookImportance parameter must be explicitly set to HookImportance.Important in the DbContextScope constructor");
    }

    #endregion

    #region Test 7: Page and chunk size is 500

    [Test]
    public void Page_and_chunk_size_is_500()
    {
        // Arrange: read the TargetGroupEvaluatorTask source file.
        var source = ReadSourceFile(
            Path.Combine("src", "Smartstore.Core", "Platform", "Identity", "Rules", "TargetGroupEvaluatorTask.cs"));

        // Assert: the ProcessFilter call uses page size 500.
        // The source should contain: ProcessFilter(expression, 0, 500)
        Assert.That(source, Does.Contain("ProcessFilter(expression, 0, 500)"),
            "ProcessFilter must be called with pageSize = 500");

        // Assert: the FastPager is created with page size 500.
        // The source should contain: new FastPager<Customer>(filterResult.SourceQuery, 500)
        Assert.That(source, Does.Contain("FastPager<Customer>(filterResult.SourceQuery, 500)"),
            "FastPager must be created with pageSize = 500");

        // Assert: chunk size for insertions is 500.
        // The source should contain: .Chunk(500)
        Assert.That(source, Does.Contain(".Chunk(500)"),
            "Customer ID chunking for bulk insertion must use chunk size 500");
    }

    #endregion
}
