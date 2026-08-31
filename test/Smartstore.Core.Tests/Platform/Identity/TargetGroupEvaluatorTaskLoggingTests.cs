using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;

namespace Smartstore.Core.Tests.Platform.Identity;

[TestFixture]
public class TargetGroupEvaluatorTaskLoggingTests : TargetGroupEvaluatorTaskTestBase
{
    private Mock<ILogger> _loggerMock;

    [SetUp]
    public void LoggingTestSetUp()
    {
        _loggerMock = new Mock<ILogger>();
        _loggerMock.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        _sut.Logger = _loggerMock.Object;
    }

    [Test]
    public async Task Run_emits_at_least_one_debug_log_entry()
    {
        var role = SeedRoleWithRuleSets("LogRole", ruleSetCount: 1);
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync((IRuleExpressionGroup)null);

        var ctx = CreateTaskExecutionContext();

        await _sut.Run(ctx, CancellationToken.None);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            "TargetGroupEvaluatorTask.Run must emit at least one debug-level log entry via ILogger");
    }

    [Test]
    public async Task Run_with_no_roles_still_emits_start_and_complete_log_entries()
    {
        var ctx = CreateTaskExecutionContext();

        await _sut.Run(ctx, CancellationToken.None);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeast(2),
            "Expected at least 2 debug log entries: one for run-start and one for run-complete");
    }
}

[TestFixture]
public class TargetGroupEvaluatorTaskLoggingParityTests : TargetGroupEvaluatorTaskTestBase
{
    private static string RepoRoot
    {
        get
        {
            var repoRoot = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            if (!File.Exists(Path.Combine(repoRoot, "Smartstore.sln")))
                throw new DirectoryNotFoundException(
                    $"Could not find Smartstore.sln at expected repo root: {repoRoot}");
            return repoRoot;
        }
    }

    private static string ReadSourceFile(string relativePath)
    {
        var fullPath = Path.Combine(RepoRoot, relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Source file not found: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    [Test]
    public void TargetGroupEvaluatorTask_has_ILogger_property_with_NullLogger_default()
    {
        var source = ReadSourceFile(Path.Combine(
            "src", "Smartstore.Core", "Platform", "Identity", "Rules",
            "TargetGroupEvaluatorTask.cs"));
        Assert.That(source, Does.Contain("public ILogger Logger { get; set; } = NullLogger.Instance;"),
            "TargetGroupEvaluatorTask must declare the Autofac-injectable ILogger property");
    }

    [Test]
    public void TargetGroupEvaluatorTask_defines_required_LoggerMessage_delegates()
    {
        var source = ReadSourceFile(Path.Combine(
            "src", "Smartstore.Core", "Platform", "Identity", "Rules",
            "TargetGroupEvaluatorTask.cs"));

        foreach (var delegateName in new[]
        {
            "_logRunStarted", "_logBulkDeleted", "_logProcessingRole",
            "_logRuleEvaluationResult", "_logChunkInserted",
            "_logEntityDetachment", "_logCacheInvalidation", "_logRunCompleted"
        })
        {
            Assert.That(source, Does.Contain(delegateName),
                $"TargetGroupEvaluatorTask must define the '{delegateName}' LoggerMessage.Define delegate");
        }
    }

    [Test]
    public void TargetGroupEvaluatorTask_no_legacy_debug_writeline()
    {
        var source = ReadSourceFile(Path.Combine(
            "src", "Smartstore.Core", "Platform", "Identity", "Rules",
            "TargetGroupEvaluatorTask.cs"));
        Assert.That(source, Does.Not.Contain("Debug.WriteLineIf"),
            "Legacy Debug.WriteLineIf must be removed from TargetGroupEvaluatorTask");
        Assert.That(source, Does.Not.Contain("Debug.WriteLine("),
            "Legacy Debug.WriteLine must be removed from TargetGroupEvaluatorTask");
    }

    [Test]
    public void TargetGroupService_has_ILogger_property_with_NullLogger_default()
    {
        var source = ReadSourceFile(Path.Combine(
            "src", "Smartstore.Core", "Platform", "Identity", "Rules",
            "TargetGroupService.cs"));
        Assert.That(source, Does.Contain("public ILogger Logger { get; set; } = NullLogger.Instance;"),
            "TargetGroupService must declare the Autofac-injectable ILogger property");
    }

    [Test]
    public void DbTaskStore_has_ILogger_property_with_NullLogger_default()
    {
        var source = ReadSourceFile(Path.Combine(
            "src", "Smartstore.Core", "Platform", "Scheduling", "Services",
            "DbTaskStore.cs"));
        Assert.That(source, Does.Contain("public ILogger Logger { get; set; } = NullLogger.Instance;"),
            "DbTaskStore must declare the Autofac-injectable ILogger property");
    }

    [Test]
    public void RuleService_has_ILogger_property_with_NullLogger_default()
    {
        var source = ReadSourceFile(Path.Combine(
            "src", "Smartstore.Core", "Platform", "Rules", "Services",
            "RuleService.cs"));
        Assert.That(source, Does.Contain("public ILogger Logger { get; set; } = NullLogger.Instance;"),
            "RuleService must declare the Autofac-injectable ILogger property");
    }

    [Test]
    public void Appsettings_has_namespace_scoped_debug_overrides()
    {
        var source = ReadSourceFile(Path.Combine(
            "src", "Smartstore.Web", "appsettings.json"));
        Assert.That(source, Does.Contain("Smartstore.Core.Identity"),
            "appsettings.json must have a Serilog override for the Identity namespace");
        Assert.That(source, Does.Contain("Smartstore.Scheduling"),
            "appsettings.json must have a Serilog override for the Scheduling namespace");
        Assert.That(source, Does.Contain("\"Debug\""),
            "appsettings.json must set Debug level for the overridden namespaces");
    }

    [Test]
    public void Appsettings_database_sink_at_information_level()
    {
        var source = ReadSourceFile(Path.Combine(
            "src", "Smartstore.Web", "appsettings.json"));
        Assert.That(source, Does.Contain("\"Database\": \"Information\""),
            "appsettings.json must keep the Database sink at Information level");
    }
}
