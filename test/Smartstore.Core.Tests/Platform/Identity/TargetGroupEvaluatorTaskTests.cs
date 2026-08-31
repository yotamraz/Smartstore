using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using NUnit.Framework;
using Smartstore.Caching;
using Smartstore.Collections;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Core.Security;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Scheduling;
using Smartstore.Threading;

namespace Smartstore.Core.Tests.Platform.Identity;

[TestFixture]
public class TargetGroupEvaluatorTaskTests : ServiceTestBase
{
    private SqliteConnection _sqliteConnection;
    private SmartDbContext _sqliteDb;

    private Mock<IRuleService> _ruleServiceMock;
    private Mock<ITargetGroupService> _targetGroupServiceMock;
    private Mock<IRuleProviderFactory> _ruleProviderFactoryMock;
    private Mock<ICacheManager> _cacheMock;
    private Mock<ITaskStore> _taskStoreMock;
    private Mock<IAsyncState> _asyncStateMock;

    private TargetGroupEvaluatorTask _sut;

    [SetUp]
    public void TestSetUp()
    {
        // Create a persistent in-memory SQLite connection (supports ExecuteDeleteAsync,
        // unlike the InMemory provider used by ServiceTestBase).
        _sqliteConnection = new SqliteConnection("DataSource=:memory:");
        _sqliteConnection.Open();

        var sqliteFactory = new SqliteTestDbFactory(_sqliteConnection);

        var optionsBuilder = new DbContextOptionsBuilder<SmartDbContext>()
            .UseDbFactory(sqliteFactory, "DataSource=:memory:", factoryBuilder =>
            {
                factoryBuilder.AddModelAssemblies(new[]
                {
                    typeof(SmartDbContext).Assembly
                });
            });

        _sqliteDb = new SmartDbContext((DbContextOptions<SmartDbContext>)optionsBuilder.Options);
        _sqliteDb.Database.EnsureCreated();

        _ruleServiceMock = new Mock<IRuleService>();
        _targetGroupServiceMock = new Mock<ITargetGroupService>();
        _ruleProviderFactoryMock = new Mock<IRuleProviderFactory>();
        _cacheMock = new Mock<ICacheManager>();
        _taskStoreMock = new Mock<ITaskStore>();
        _asyncStateMock = new Mock<IAsyncState>();

        _ruleProviderFactoryMock
            .Setup(x => x.GetProvider(RuleScope.Customer, null))
            .Returns(_targetGroupServiceMock.Object);

        _cacheMock
            .Setup(x => x.RemoveByPatternAsync(It.IsAny<string>()))
            .ReturnsAsync(0);

        _taskStoreMock
            .Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()))
            .Returns(Task.CompletedTask);

        _sut = new TargetGroupEvaluatorTask(
            _sqliteDb,
            _cacheMock.Object,
            _ruleServiceMock.Object,
            _ruleProviderFactoryMock.Object);
    }

    [TearDown]
    public void TestTearDown()
    {
        _sqliteDb?.Dispose();
        _sqliteConnection?.Dispose();
    }

    #region Helpers

    private TaskExecutionContext CreateTaskExecutionContext(IDictionary<string, string> taskParameters = null)
    {
        var taskDescriptor = new TaskDescriptor
        {
            Name = "TargetGroupEvaluator",
            Type = typeof(TargetGroupEvaluatorTask).AssemblyQualifiedName,
            Enabled = true,
            CronExpression = "0 */6 * * *",
        };

        var executionInfo = new TaskExecutionInfo
        {
            TaskDescriptorId = 1,
            IsRunning = true,
            MachineName = "TEST",
            StartedOnUtc = DateTime.UtcNow,
            Task = taskDescriptor,
        };

        var httpContext = new DefaultHttpContext();
        var componentContextMock = new Mock<IComponentContext>();

        return new TaskExecutionContext(
            _taskStoreMock.Object,
            _asyncStateMock.Object,
            httpContext,
            componentContextMock.Object,
            executionInfo,
            taskParameters);
    }

    /// <summary>
    /// Seeds a customer role with associated active rule sets into the database.
    /// Returns the CustomerRole entity after it has been saved.
    /// </summary>
    private CustomerRole SeedRoleWithRuleSets(string systemName, int ruleSetCount, bool ruleSetActive = true)
    {
        var role = new CustomerRole
        {
            Name = systemName,
            SystemName = systemName,
            Active = true,
        };

        for (int i = 0; i < ruleSetCount; i++)
        {
            var ruleSet = new RuleSetEntity
            {
                Name = $"{systemName}_RuleSet_{i}",
                IsActive = ruleSetActive,
                Scope = RuleScope.Customer,
            };

            // Add a dummy rule to the rule set.
            ruleSet.Rules.Add(new RuleEntity
            {
                RuleType = "TestRule",
                Operator = "Is",
                Value = "TestValue",
            });

            role.RuleSets.Add(ruleSet);
        }

        _sqliteDb.CustomerRoles.Add(role);
        _sqliteDb.SaveChanges();

        return role;
    }

    /// <summary>
    /// Seeds customers into the database and returns the list.
    /// </summary>
    private List<Customer> SeedCustomers(int count)
    {
        var customers = new List<Customer>();
        for (int i = 0; i < count; i++)
        {
            var customer = new Customer
            {
                CustomerGuid = Guid.NewGuid(),
                CreatedOnUtc = DateTime.UtcNow,
                LastActivityDateUtc = DateTime.UtcNow,
            };
            customers.Add(customer);
        }

        _sqliteDb.Customers.AddRange(customers);
        _sqliteDb.SaveChanges();

        return customers;
    }

    /// <summary>
    /// Seeds existing system mappings into the database.
    /// </summary>
    private void SeedSystemMappings(int roleId, IEnumerable<int> customerIds)
    {
        foreach (var customerId in customerIds)
        {
            _sqliteDb.CustomerRoleMappings.Add(new CustomerRoleMapping
            {
                CustomerId = customerId,
                CustomerRoleId = roleId,
                IsSystemMapping = true,
            });
        }

        _sqliteDb.SaveChanges();
    }

    /// <summary>
    /// Creates a mock IPagedList backed by a real DbContext queryable so FastPager can iterate.
    /// </summary>
    private IPagedList<Customer> CreatePagedListFromQuery(IQueryable<Customer> sourceQuery)
    {
        var pagedListMock = new Mock<IPagedList<Customer>>();
        pagedListMock.Setup(x => x.SourceQuery).Returns(sourceQuery);
        return pagedListMock.Object;
    }

    /// <summary>
    /// Sets up the rule service mock to return a FilterExpression for a given rule set.
    /// </summary>
    private void SetupRuleServiceReturnsFilterExpression(RuleSetEntity ruleSet)
    {
        var filterExpression = new FilterExpressionGroup(typeof(Customer));

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == ruleSet.Id),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync(filterExpression);
    }

    /// <summary>
    /// Sets up the target group service mock to return a paged list of customers with SourceQuery
    /// backed by the DbContext for proper FastPager iteration.
    /// </summary>
    private void SetupTargetGroupServiceReturnsCustomers(IEnumerable<int> customerIds)
    {
        var customerIdSet = customerIds.ToHashSet();
        var sourceQuery = _sqliteDb.Customers.Where(c => customerIdSet.Contains(c.Id));
        var pagedList = CreatePagedListFromQuery(sourceQuery);

        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(pagedList);
    }

    /// <summary>
    /// Sets up the target group service to return an empty result.
    /// </summary>
    private void SetupTargetGroupServiceReturnsEmpty()
    {
        var sourceQuery = _sqliteDb.Customers.Where(c => false);
        var pagedList = CreatePagedListFromQuery(sourceQuery);

        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(pagedList);
    }

    #endregion

    [Test]
    public async Task Correct_execution_sequence_delete_evaluate_insert()
    {
        // Arrange: seed a role with one active rule set and some customers.
        var role = SeedRoleWithRuleSets("VipMembers", ruleSetCount: 1);
        var customers = SeedCustomers(3);

        // Seed existing system mappings that should be deleted first.
        SeedSystemMappings(role.Id, customers.Select(c => c.Id).Take(1));

        var mappingCountAtEvaluate = -1;
        var callLog = new List<string>();

        // Set up rule evaluation — snapshot mapping count to prove delete ran first.
        foreach (var ruleSet in role.RuleSets)
        {
            var filterExpression = new FilterExpressionGroup(typeof(Customer));
            _ruleServiceMock
                .Setup(x => x.CreateExpressionGroupAsync(
                    It.Is<RuleSetEntity>(rs => rs.Id == ruleSet.Id),
                    It.IsAny<IRuleVisitor>(),
                    It.IsAny<bool>()))
                .ReturnsAsync(() =>
                {
                    mappingCountAtEvaluate = _sqliteDb.CustomerRoleMappings
                        .Count(m => m.CustomerRoleId == role.Id && m.IsSystemMapping);
                    callLog.Add("evaluate");
                    return filterExpression;
                });
        }

        // Set up target group service — track that process-filter ran.
        var customerIdSet = customers.Select(c => c.Id).ToHashSet();
        var sourceQuery = _sqliteDb.Customers.Where(c => customerIdSet.Contains(c.Id));
        var pagedListMock = new Mock<IPagedList<Customer>>();
        pagedListMock.Setup(x => x.SourceQuery).Returns(sourceQuery);
        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(() =>
            {
                callLog.Add("process-filter");
                return pagedListMock.Object;
            });

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert ordering
        var mappingCountAfterRun = _sqliteDb.CustomerRoleMappings
            .Count(m => m.CustomerRoleId == role.Id && m.IsSystemMapping);

        Assert.Multiple(() =>
        {
            // Delete ran before evaluate: no system mappings existed at evaluate time.
            Assert.That(mappingCountAtEvaluate, Is.EqualTo(0),
                "System mappings should be 0 at evaluate time (delete before evaluate).");
            // Evaluate ran before insert: process-filter was called.
            Assert.That(callLog, Does.Contain("evaluate"));
            Assert.That(callLog, Does.Contain("process-filter"));
            Assert.That(callLog.IndexOf("evaluate"), Is.LessThan(callLog.IndexOf("process-filter")));
            // Insert completed: new mappings exist.
            Assert.That(mappingCountAfterRun, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task CustomerRoleIds_parameter_scopes_deletion_and_role_processing()
    {
        // Arrange: seed two roles with rule sets.
        var role1 = SeedRoleWithRuleSets("Role1", ruleSetCount: 1);
        var role2 = SeedRoleWithRuleSets("Role2", ruleSetCount: 1);
        var customers = SeedCustomers(2);

        // Seed system mappings for both roles.
        SeedSystemMappings(role1.Id, customers.Select(c => c.Id));
        SeedSystemMappings(role2.Id, customers.Select(c => c.Id));

        // Set up rule evaluation only for role1's rule sets.
        foreach (var ruleSet in role1.RuleSets)
        {
            SetupRuleServiceReturnsFilterExpression(ruleSet);
        }

        SetupTargetGroupServiceReturnsCustomers(customers.Select(c => c.Id));

        // Pass only role1's ID in parameters.
        var parameters = new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = role1.Id.ToString()
        };

        var ctx = CreateTaskExecutionContext(parameters);

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert
        Assert.Multiple(() =>
        {
            // Role1 mappings should be recreated.
            var role1Mappings = _sqliteDb.CustomerRoleMappings
                .Where(m => m.CustomerRoleId == role1.Id && m.IsSystemMapping)
                .ToList();
            Assert.That(role1Mappings, Has.Count.EqualTo(2));

            // Role2 mappings should be untouched (still exist from seed).
            var role2Mappings = _sqliteDb.CustomerRoleMappings
                .Where(m => m.CustomerRoleId == role2.Id && m.IsSystemMapping)
                .ToList();
            Assert.That(role2Mappings, Has.Count.EqualTo(2));

            // Verify rule service was NOT called for role2's rule sets.
            _ruleServiceMock.Verify(
                x => x.CreateExpressionGroupAsync(
                    It.Is<RuleSetEntity>(rs => role2.RuleSets.Any(r => r.Id == rs.Id)),
                    It.IsAny<IRuleVisitor>(),
                    It.IsAny<bool>()),
                Times.Never);
        });
    }

    [Test]
    public async Task Progress_reported_per_role()
    {
        // Arrange: seed 3 roles with rule sets.
        var role1 = SeedRoleWithRuleSets("RoleA", ruleSetCount: 1);
        var role2 = SeedRoleWithRuleSets("RoleB", ruleSetCount: 1);
        var role3 = SeedRoleWithRuleSets("RoleC", ruleSetCount: 1);

        // Set up rule evaluation to return null (no filters matched) for simplicity.
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync((IRuleExpressionGroup)null);

        var capturedProgressValues = new List<int>();
        _taskStoreMock
            .Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()))
            .Callback<TaskExecutionInfo>(info => capturedProgressValues.Add(info.ProgressPercent ?? 0))
            .Returns(Task.CompletedTask);

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: progress percentages should be 33%, 67%, 100% (one per role).
        Assert.That(capturedProgressValues, Has.Count.EqualTo(3));
        Assert.That(capturedProgressValues, Is.EqualTo(new[] { 33, 67, 100 }));
    }

    [Test]
    public async Task Cancellation_respected_between_ruleset_evaluations()
    {
        // Arrange: seed a role with 2 active rule sets.
        var role = SeedRoleWithRuleSets("CancellableRole", ruleSetCount: 2);

        // Set up rule service: first call succeeds, then cancellation fires before second call.
        var cts = new CancellationTokenSource();
        var callCount = 0;

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync((RuleSetEntity rs, IRuleVisitor v, bool h) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Cancel after the first rule set evaluation.
                    cts.Cancel();
                }

                return null;
            });

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, cts.Token);

        // Assert: the task should return early. Because the cancellation check is BEFORE
        // CreateExpressionGroupAsync for the second rule set, CreateExpressionGroupAsync
        // should only be called once.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()),
            Times.Once);
    }

    [Test]
    public async Task Cancellation_respected_between_chunk_insertions()
    {
        // Arrange: seed a role with 1 rule set and enough customers to require multiple chunks.
        // The task uses chunks of 500 for insertion.
        var role = SeedRoleWithRuleSets("ChunkCancelRole", ruleSetCount: 1);

        // Seed 1001 customers to force 3 chunks (500 + 500 + 1).
        var customers = SeedCustomers(1001);

        foreach (var ruleSet in role.RuleSets)
        {
            SetupRuleServiceReturnsFilterExpression(ruleSet);
        }

        SetupTargetGroupServiceReturnsCustomers(customers.Select(c => c.Id));

        // Use a CancellationTokenSource that we cancel deterministically.
        // The task checks cancelToken.IsCancellationRequested at the top of each chunk iteration.
        // We use a SaveChanges interceptor to cancel after the first chunk commit,
        // so the second chunk iteration's cancellation check fires.
        var cts = new CancellationTokenSource();
        var commitCount = 0;
        var interceptor = new TestSaveChangesInterceptor(() =>
        {
            commitCount++;
            if (commitCount >= 1)
            {
                cts.Cancel();
            }
        });

        // Recreate the context with the interceptor.
        _sqliteDb.Dispose();
        var optionsBuilder = new DbContextOptionsBuilder<SmartDbContext>()
            .AddInterceptors(interceptor)
            .UseDbFactory(new SqliteTestDbFactory(_sqliteConnection), "DataSource=:memory:", factoryBuilder =>
            {
                factoryBuilder.AddModelAssemblies(new[]
                {
                    typeof(SmartDbContext).Assembly
                });
            });

        _sqliteDb = new SmartDbContext((DbContextOptions<SmartDbContext>)optionsBuilder.Options);

        // Recreate the SUT with the new context.
        _sut = new TargetGroupEvaluatorTask(
            _sqliteDb,
            _cacheMock.Object,
            _ruleServiceMock.Object,
            _ruleProviderFactoryMock.Object);

        // Re-setup the mock since the SourceQuery must reference the new context.
        SetupTargetGroupServiceReturnsCustomers(customers.Select(c => c.Id));

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, cts.Token);

        // Assert: some mappings were created but not all 1001.
        var totalMappings = _sqliteDb.CustomerRoleMappings
            .Count(m => m.CustomerRoleId == role.Id && m.IsSystemMapping);

        Assert.That(totalMappings, Is.LessThan(1001));
        // The first chunk of 500 should have been committed.
        Assert.That(totalMappings, Is.GreaterThanOrEqualTo(500));
    }

    [Test]
    public async Task Cache_cleared_when_mappings_changed()
    {
        // Arrange: seed a role with a rule set and customers.
        var role = SeedRoleWithRuleSets("CacheRole", ruleSetCount: 1);
        var customers = SeedCustomers(2);

        // Seed existing system mappings (so numDeleted > 0).
        SeedSystemMappings(role.Id, customers.Select(c => c.Id));

        foreach (var ruleSet in role.RuleSets)
        {
            SetupRuleServiceReturnsFilterExpression(ruleSet);
        }

        SetupTargetGroupServiceReturnsCustomers(customers.Select(c => c.Id));

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: cache should be cleared since mappings were changed.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN),
            Times.Once);
    }

    [Test]
    public async Task Cache_not_cleared_when_no_changes()
    {
        // Arrange: no roles with active rule sets => nothing gets deleted or added.
        // Seed a role without rule sets.
        var role = new CustomerRole
        {
            Name = "NoCacheRole",
            SystemName = "NoCacheRole",
            Active = true,
        };
        _sqliteDb.CustomerRoles.Add(role);
        _sqliteDb.SaveChanges();

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: cache should NOT be cleared since no mappings were changed.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task Roles_without_active_rulesets_skipped()
    {
        // Arrange: seed a role with only inactive rule sets.
        var role = SeedRoleWithRuleSets("InactiveRuleRole", ruleSetCount: 2, ruleSetActive: false);
        SeedCustomers(2);

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: the role should not be loaded for processing because its rule sets are inactive.
        // The query filters by x.RuleSets.Any(y => y.IsActive), so this role won't be in the results.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()),
            Times.Never);

        // Verify no progress was reported (no roles processed).
        _taskStoreMock.Verify(
            x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()),
            Times.Never);

        // Verify no mappings were created.
        var mappings = _sqliteDb.CustomerRoleMappings.Where(m => m.IsSystemMapping).ToList();
        Assert.That(mappings, Has.Count.EqualTo(0));
    }

    [Test]
    public async Task Empty_rule_evaluation_produces_no_mappings()
    {
        // Arrange: seed a role with a rule set, but rule service returns null.
        var role = SeedRoleWithRuleSets("EmptyEvalRole", ruleSetCount: 1);
        SeedCustomers(5);

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync((IRuleExpressionGroup)null);

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: no system mappings should be created.
        var mappings = _sqliteDb.CustomerRoleMappings.Where(m => m.IsSystemMapping).ToList();
        Assert.That(mappings, Has.Count.EqualTo(0));

        // Target group service should not have been called since expression was null.
        _targetGroupServiceMock.Verify(
            x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Never);
    }

    [Test]
    public async Task Empty_target_group_result_deletes_old_mappings_adds_none()
    {
        // Arrange: seed a role with a rule set, customers, and existing system mappings.
        var role = SeedRoleWithRuleSets("EmptyResultRole", ruleSetCount: 1);
        var customers = SeedCustomers(3);
        SeedSystemMappings(role.Id, customers.Select(c => c.Id));

        foreach (var ruleSet in role.RuleSets)
        {
            SetupRuleServiceReturnsFilterExpression(ruleSet);
        }

        // Target group service returns zero customers.
        SetupTargetGroupServiceReturnsEmpty();

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: old system mappings deleted, no new ones created.
        var mappings = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == role.Id && m.IsSystemMapping)
            .ToList();
        Assert.That(mappings, Has.Count.EqualTo(0));

        // Cache should still be cleared because numDeleted > 0.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN),
            Times.Once);
    }

    [Test]
    public async Task Empty_rule_evaluation_with_non_filter_expression_produces_no_mappings()
    {
        // Arrange: seed a role with a rule set, but rule service returns a non-FilterExpression
        // (e.g., a RuleExpressionGroup that is NOT a FilterExpression).
        var role = SeedRoleWithRuleSets("NonFilterRole", ruleSetCount: 1);
        SeedCustomers(3);

        var nonFilterGroup = new RuleExpressionGroup();

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync(nonFilterGroup);

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: no system mappings should be created.
        var mappings = _sqliteDb.CustomerRoleMappings.Where(m => m.IsSystemMapping).ToList();
        Assert.That(mappings, Has.Count.EqualTo(0));

        // Target group service should not have been called since expression was not a FilterExpression.
        _targetGroupServiceMock.Verify(
            x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Never);
    }
}

/// <summary>
/// An EF Core SaveChanges interceptor that invokes a callback after each successful SaveChangesAsync.
/// Used to deterministically trigger cancellation between chunk commits.
/// </summary>
internal class TestSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly Action _onSavedChanges;

    public TestSaveChangesInterceptor(Action onSavedChanges)
    {
        _onSavedChanges = onSavedChanges;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        _onSavedChanges();
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        _onSavedChanges();
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }
}
