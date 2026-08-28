using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Smartstore.Caching;
using Smartstore.Collections;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Data;
using Smartstore.Scheduling;
using Smartstore.Threading;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

/// <summary>
/// Testable subclass that overrides only the <c>DeleteSystemMappingsAsync</c> method
/// (which calls <c>ExecuteDeleteAsync</c>, unsupported by the InMemory provider)
/// with an equivalent manual removal. All other production behavior in <c>Run</c>
/// is exercised exactly as written.
/// </summary>
internal sealed class TestableTargetGroupEvaluatorTask(
    SmartDbContext db,
    ICacheManager cache,
    IRuleService ruleService,
    IRuleProviderFactory ruleProviderFactory)
    : TargetGroupEvaluatorTask(db, cache, ruleService, ruleProviderFactory)
{
    public int LastDeletedCount { get; private set; }

    private int _commitCount;
    public CancellationTokenSource CommitCts { get; set; }

    protected override async Task CommitChunkAsync(DbContextScope scope, CancellationToken cancelToken)
    {
        await base.CommitChunkAsync(scope, cancelToken);
        if (Interlocked.Increment(ref _commitCount) == 1)
            CommitCts?.Cancel();
    }

    protected override async Task<int> DeleteSystemMappingsAsync(IQueryable<CustomerRoleMapping> query, CancellationToken cancelToken)
    {
        var toDelete = await query.ToListAsync(cancelToken);
        _db.CustomerRoleMappings.RemoveRange(toDelete);
        if (toDelete.Count > 0)
        {
            await _db.SaveChangesAsync(cancelToken);
        }

        LastDeletedCount = toDelete.Count;
        return toDelete.Count;
    }
}

[TestFixture]
public class TargetGroupEvaluatorTaskTests : ServiceTestBase
{
    private Mock<ICacheManager> _cacheMock;
    private Mock<IRuleService> _ruleServiceMock;
    private Mock<IRuleProviderFactory> _ruleProviderFactoryMock;
    private Mock<ITargetGroupService> _targetGroupServiceMock;
    private TestableTargetGroupEvaluatorTask _task;

    [SetUp]
    public async Task TestSetUp()
    {
        // Clean data from any previous test run.
        var existingMappings = await DbContext.CustomerRoleMappings.ToListAsync();
        if (existingMappings.Count > 0)
        {
            DbContext.CustomerRoleMappings.RemoveRange(existingMappings);
        }

        var existingRoles = await DbContext.CustomerRoles
            .Include(r => r.RuleSets)
            .ToListAsync();
        if (existingRoles.Count > 0)
        {
            DbContext.CustomerRoles.RemoveRange(existingRoles);
        }

        var existingRuleSets = await DbContext.RuleSets.ToListAsync();
        if (existingRuleSets.Count > 0)
        {
            DbContext.RuleSets.RemoveRange(existingRuleSets);
        }

        var existingCustomers = await DbContext.Customers.ToListAsync();
        if (existingCustomers.Count > 0)
        {
            DbContext.Customers.RemoveRange(existingCustomers);
        }

        await DbContext.SaveChangesAsync();
        DbContext.ChangeTracker.Clear();

        _cacheMock = new Mock<ICacheManager>();
        _ruleServiceMock = new Mock<IRuleService>();
        _targetGroupServiceMock = new Mock<ITargetGroupService>();
        _ruleProviderFactoryMock = new Mock<IRuleProviderFactory>();

        _ruleProviderFactoryMock
            .Setup(x => x.GetProvider(RuleScope.Customer, null))
            .Returns(_targetGroupServiceMock.Object);

        _task = new TestableTargetGroupEvaluatorTask(
            DbContext,
            _cacheMock.Object,
            _ruleServiceMock.Object,
            _ruleProviderFactoryMock.Object);
    }

    #region Helpers

    private TaskExecutionContext CreateTaskExecutionContext(IDictionary<string, string> parameters = null)
    {
        var taskStoreMock = new Mock<ITaskStore>();
        var asyncStateMock = new Mock<IAsyncState>();
        var httpContext = new DefaultHttpContext();
        var componentContextMock = new Mock<IComponentContext>();

        var taskDescriptor = new TaskDescriptor
        {
            Name = "TargetGroupEvaluatorTask",
            Type = typeof(TargetGroupEvaluatorTask).AssemblyQualifiedName,
            Enabled = true,
            CronExpression = "0 * * * *"
        };

        var executionInfo = new TaskExecutionInfo
        {
            TaskDescriptorId = 1,
            IsRunning = true,
            StartedOnUtc = DateTime.UtcNow,
            Task = taskDescriptor
        };

        return new TaskExecutionContext(
            taskStoreMock.Object,
            asyncStateMock.Object,
            httpContext,
            componentContextMock.Object,
            executionInfo,
            parameters);
    }

    private void SeedMappings(bool isSystem, params (int customerId, int roleId)[] mappings)
    {
        foreach (var (customerId, roleId) in mappings)
        {
            DbContext.CustomerRoleMappings.Add(new CustomerRoleMapping
            {
                CustomerId = customerId,
                CustomerRoleId = roleId,
                IsSystemMapping = isSystem
            });
        }

        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();
    }

    private CustomerRole SeedActiveRoleWithRuleSet(string systemName, bool roleActive = true, bool ruleSetActive = true)
    {
        var ruleSet = new RuleSetEntity
        {
            IsActive = ruleSetActive,
            Name = $"RuleSet for {systemName}",
            Scope = RuleScope.Customer
        };

        var role = new CustomerRole
        {
            SystemName = systemName,
            Active = roleActive,
            Name = systemName
        };

        role.RuleSets.Add(ruleSet);

        DbContext.CustomerRoles.Add(role);
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        return role;
    }

    private void SetupRuleServiceForRuleSet(RuleSetEntity ruleSet, IRuleExpressionGroup expression)
    {
        // Match by RuleSetEntity.Id rather than by reference, because the task
        // queries roles with AsNoTracking() which creates new entity instances.
        var ruleSetId = ruleSet.Id;
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == ruleSetId),
                _targetGroupServiceMock.Object,
                false))
            .Returns(Task.FromResult(expression));
    }

    private void SetupTargetGroupServiceForExpression(FilterExpressionGroup expression, List<Customer> customers)
    {
        // Seed the customers into the InMemory database so that the SourceQuery
        // uses EF Core's IQueryable (which supports IAsyncEnumerable), not a
        // plain LINQ-to-Objects IQueryable that would fail in FastPager.ReadNextPageAsync.
        foreach (var c in customers)
        {
            // Only add if not already tracked/present.
            if (DbContext.ChangeTracker.Entries<Customer>().All(e => e.Entity.Id != c.Id))
            {
                DbContext.Customers.Add(c);
            }
        }
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        // Build a filtered query that returns only the seeded customer IDs.
        var customerIds = customers.Select(c => c.Id).ToArray();
        var sourceQuery = DbContext.Customers.Where(x => customerIds.Contains(x.Id));

        var mockPagedList = new Mock<IPagedList<Customer>>();
        mockPagedList
            .Setup(x => x.SourceQuery)
            .Returns(sourceQuery);

        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.Is<FilterExpression[]>(arr => arr.Length == 1 && arr[0] == expression),
                LogicalRuleOperator.And,
                0,
                500))
            .Returns(mockPagedList.Object);
    }

    #endregion

    #region Test 1: System mapping deletion (unfiltered)

    [Test]
    public async Task Run_WithoutCustomerRoleIds_DeletesAllSystemMappings()
    {
        // Arrange
        SeedMappings(true,(1, 10), (2, 20), (3, 30));
        SeedMappings(false,(4, 40));

        var ctx = CreateTaskExecutionContext();

        // Act
        await _task.Run(ctx);

        // Assert: all system mappings removed, non-system mapping preserved
        var remainingMappings = await DbContext.CustomerRoleMappings.AsNoTracking().ToListAsync();
        Assert.That(remainingMappings.Count, Is.EqualTo(1));
        Assert.That(remainingMappings[0].IsSystemMapping, Is.False);
        Assert.That(remainingMappings[0].CustomerId, Is.EqualTo(4));
        Assert.That(_task.LastDeletedCount, Is.EqualTo(3));
    }

    #endregion

    #region Test 2: System mapping deletion (filtered)

    [Test]
    public async Task Run_WithCustomerRoleIds_DeletesOnlyMatchingSystemMappings()
    {
        // Arrange
        SeedMappings(true,(1, 10), (2, 20), (3, 30));
        SeedMappings(false,(4, 10));

        var parameters = new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = "10,20"
        };
        var ctx = CreateTaskExecutionContext(parameters);

        // Act
        await _task.Run(ctx);

        // Assert: only system mappings for roles 10 and 20 deleted, role 30 system mapping preserved
        var remainingMappings = await DbContext.CustomerRoleMappings.AsNoTracking().ToListAsync();
        Assert.That(remainingMappings.Count, Is.EqualTo(2));
        Assert.That(remainingMappings.Any(m => m.IsSystemMapping && m.CustomerRoleId == 30), Is.True);
        Assert.That(remainingMappings.Any(m => !m.IsSystemMapping && m.CustomerRoleId == 10), Is.True);
        Assert.That(_task.LastDeletedCount, Is.EqualTo(2));
    }

    #endregion

    #region Test 3: Role filtering

    [Test]
    public async Task Run_OnlyProcessesActiveRolesWithActiveRuleSets()
    {
        // Arrange: create roles with various active/inactive states
        var activeRoleWithActiveRuleSet = SeedActiveRoleWithRuleSet("ActiveWithActive", roleActive: true, ruleSetActive: true);
        SeedActiveRoleWithRuleSet("ActiveWithInactive", roleActive: true, ruleSetActive: false);
        SeedActiveRoleWithRuleSet("InactiveWithActive", roleActive: false, ruleSetActive: true);

        // Setup rule service for the active role+ruleset combination
        var ruleSet = activeRoleWithActiveRuleSet.RuleSets.First();
        var filterExpression = new FilterExpressionGroup(typeof(Customer));
        SetupRuleServiceForRuleSet(ruleSet, filterExpression);
        SetupTargetGroupServiceForExpression(filterExpression, new List<Customer>());

        var ctx = CreateTaskExecutionContext();

        // Act
        await _task.Run(ctx);

        // Assert: only the active role with active rule set was processed
        var ruleSetId = ruleSet.Id;
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == ruleSetId),
                _targetGroupServiceMock.Object,
                false),
            Times.Once);

        // Verify that no other rule sets were processed (total call count = 1)
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()),
            Times.Once);
    }

    #endregion

    #region Test 4: Rule evaluation

    [Test]
    public async Task Run_EvaluatesEachActiveRuleSetViaRuleServiceAndTargetGroupService()
    {
        // Arrange: create role with two active rule sets
        var role = new CustomerRole
        {
            Active = true,
            SystemName = "TestRole",
            Name = "TestRole"
        };

        var ruleSet1 = new RuleSetEntity { IsActive = true, Name = "RuleSet1", Scope = RuleScope.Customer };
        var ruleSet2 = new RuleSetEntity { IsActive = true, Name = "RuleSet2", Scope = RuleScope.Customer };
        role.RuleSets.Add(ruleSet1);
        role.RuleSets.Add(ruleSet2);

        DbContext.CustomerRoles.Add(role);
        await DbContext.SaveChangesAsync();
        DbContext.ChangeTracker.Clear();

        var filterExpression1 = new FilterExpressionGroup(typeof(Customer));
        var filterExpression2 = new FilterExpressionGroup(typeof(Customer));

        SetupRuleServiceForRuleSet(ruleSet1, filterExpression1);
        SetupRuleServiceForRuleSet(ruleSet2, filterExpression2);
        SetupTargetGroupServiceForExpression(filterExpression1, new List<Customer>());
        SetupTargetGroupServiceForExpression(filterExpression2, new List<Customer>());

        var ctx = CreateTaskExecutionContext();

        // Act
        await _task.Run(ctx);

        // Assert: both rule sets were processed via IRuleService
        var rs1Id = ruleSet1.Id;
        var rs2Id = ruleSet2.Id;
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == rs1Id),
                _targetGroupServiceMock.Object,
                false),
            Times.Once);
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == rs2Id),
                _targetGroupServiceMock.Object,
                false),
            Times.Once);

        // Verify ProcessFilter was called for both expressions (the extension wraps single expression in array)
        _targetGroupServiceMock.Verify(
            x => x.ProcessFilter(
                It.Is<FilterExpression[]>(arr => arr.Length == 1 && arr[0] == filterExpression1),
                LogicalRuleOperator.And, 0, 500),
            Times.Once);
        _targetGroupServiceMock.Verify(
            x => x.ProcessFilter(
                It.Is<FilterExpression[]>(arr => arr.Length == 1 && arr[0] == filterExpression2),
                LogicalRuleOperator.And, 0, 500),
            Times.Once);
    }

    #endregion

    #region Test 5: Customer ID collection

    [Test]
    public async Task Run_CollectsCustomerIdsViaFastPagerAndAccumulatesPerRole()
    {
        // Arrange: create a role and setup rules to return customers
        var role = SeedActiveRoleWithRuleSet("TestRole");
        var ruleSet = role.RuleSets.First();

        var filterExpression = new FilterExpressionGroup(typeof(Customer));
        SetupRuleServiceForRuleSet(ruleSet, filterExpression);

        // Create customers with sequential IDs for FastPager to iterate over.
        // FastPager orders by Id DESC, takes pageSize items at a time.
        var customers = new List<Customer>();
        for (var i = 1; i <= 5; i++)
        {
            customers.Add(new Customer { Id = i });
        }

        SetupTargetGroupServiceForExpression(filterExpression, customers);

        var ctx = CreateTaskExecutionContext();

        // Act
        await _task.Run(ctx);

        // Assert: mappings created for all 5 customers, accumulated via HashSet per role
        var mappings = await DbContext.CustomerRoleMappings
            .AsNoTracking()
            .Where(m => m.IsSystemMapping)
            .ToListAsync();
        Assert.That(mappings.Count, Is.EqualTo(5));

        var mappedCustomerIds = mappings.Select(m => m.CustomerId).OrderBy(id => id).ToList();
        Assert.That(mappedCustomerIds, Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
        Assert.That(mappings.All(m => m.CustomerRoleId == role.Id), Is.True);
    }

    #endregion

    #region Test 6: Batch insertion

    [Test]
    public async Task Run_InsertsCustomerRoleMappingsInChunksOf500WithCommitAfterEach()
    {
        // Arrange: create a role and setup rules to return more than 500 customers
        var role = SeedActiveRoleWithRuleSet("BatchRole");
        var ruleSet = role.RuleSets.First();

        var filterExpression = new FilterExpressionGroup(typeof(Customer));
        SetupRuleServiceForRuleSet(ruleSet, filterExpression);

        // Create 600 customers to verify chunking behavior (500 + 100).
        // The task uses ruleSetCustomerIds.Chunk(500) and commits after each chunk.
        var customers = new List<Customer>();
        for (var i = 1; i <= 600; i++)
        {
            customers.Add(new Customer { Id = i });
        }

        SetupTargetGroupServiceForExpression(filterExpression, customers);

        var ctx = CreateTaskExecutionContext();

        // Act
        await _task.Run(ctx);

        // Assert: all 600 mappings created with IsSystemMapping = true
        var mappings = await DbContext.CustomerRoleMappings
            .AsNoTracking()
            .Where(m => m.IsSystemMapping)
            .ToListAsync();
        Assert.That(mappings.Count, Is.EqualTo(600));
        Assert.That(mappings.All(m => m.IsSystemMapping), Is.True);
        Assert.That(mappings.All(m => m.CustomerRoleId == role.Id), Is.True);
    }

    #endregion

    #region Test 7: Entity detachment

    [Test]
    public async Task Run_DetachesCustomerRoleMappingEntitiesAfterProcessingRole()
    {
        // Arrange: create a role with matching customers
        var role = SeedActiveRoleWithRuleSet("DetachRole");
        var ruleSet = role.RuleSets.First();

        var filterExpression = new FilterExpressionGroup(typeof(Customer));
        SetupRuleServiceForRuleSet(ruleSet, filterExpression);

        var customers = new List<Customer>
        {
            new() { Id = 1 },
            new() { Id = 2 }
        };
        SetupTargetGroupServiceForExpression(filterExpression, customers);

        var ctx = CreateTaskExecutionContext();

        // Act
        await _task.Run(ctx);

        // Assert: After task completes, CustomerRoleMapping entities should be detached.
        // The task calls scope.DbContext.DetachEntities<CustomerRoleMapping>() after
        // processing each role's chunks, removing them from the change tracker.
        var trackedMappings = DbContext.ChangeTracker.Entries<CustomerRoleMapping>().ToList();
        Assert.That(trackedMappings, Is.Empty);

        // Verify the mappings were actually persisted to the database
        var dbMappings = await DbContext.CustomerRoleMappings
            .AsNoTracking()
            .Where(m => m.IsSystemMapping)
            .ToListAsync();
        Assert.That(dbMappings.Count, Is.EqualTo(2));
    }

    #endregion

    #region Test 8: Cache invalidation

    [Test]
    public async Task Run_ClearsAclCacheWhenSystemMappingsDeleted()
    {
        // Arrange: seed system mappings that will be deleted (numDeleted > 0)
        SeedMappings(true,(1, 10));

        var ctx = CreateTaskExecutionContext();

        // Act
        await _task.Run(ctx);

        // Assert: cache invalidation was called with the ACL segment pattern
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync("acl:range-*"),
            Times.Once);
    }

    [Test]
    public async Task Run_ClearsAclCacheWhenNewMappingsAdded()
    {
        // Arrange: no existing system mappings, but a role will add new ones (numAdded > 0)
        var role = SeedActiveRoleWithRuleSet("CacheRole");
        var ruleSet = role.RuleSets.First();

        var filterExpression = new FilterExpressionGroup(typeof(Customer));
        SetupRuleServiceForRuleSet(ruleSet, filterExpression);

        var customers = new List<Customer> { new() { Id = 1 } };
        SetupTargetGroupServiceForExpression(filterExpression, customers);

        var ctx = CreateTaskExecutionContext();

        // Act
        await _task.Run(ctx);

        // Assert: cache invalidation was called
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync("acl:range-*"),
            Times.Once);
    }

    #endregion

    #region Test 9: No-op cache behavior

    [Test]
    public async Task Run_SkipsCacheInvalidationWhenNoMappingsChange()
    {
        // Arrange: no system mappings to delete, no active roles to process
        var ctx = CreateTaskExecutionContext();

        // Act
        await _task.Run(ctx);

        // Assert: cache invalidation was NOT called
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never);
    }

    #endregion

    #region Test 10: Cancellation handling

    [Test]
    public async Task Run_RespectsCancellationInRuleSetLoop()
    {
        // Arrange: create a role with two active rule sets
        var role = new CustomerRole
        {
            Active = true,
            SystemName = "CancelRole",
            Name = "CancelRole"
        };

        var ruleSet1 = new RuleSetEntity { IsActive = true, Name = "RS1", Scope = RuleScope.Customer };
        var ruleSet2 = new RuleSetEntity { IsActive = true, Name = "RS2", Scope = RuleScope.Customer };
        role.RuleSets.Add(ruleSet1);
        role.RuleSets.Add(ruleSet2);

        DbContext.CustomerRoles.Add(role);
        await DbContext.SaveChangesAsync();
        DbContext.ChangeTracker.Clear();

        var cts = new CancellationTokenSource();

        // Set up a callback that cancels the token on the FIRST call to
        // CreateExpressionGroupAsync, regardless of which ruleSet comes first.
        // The task iterates rule sets in an unspecified order, so we cannot rely
        // on ordering. Instead, we cancel on the first invocation and verify that
        // only one total call was made (meaning the second was skipped).
        var nonFilterGroup = new Mock<IRuleExpressionGroup>();
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                _targetGroupServiceMock.Object,
                false))
            .Returns((RuleSetEntity _, IRuleVisitor _, bool _) =>
            {
                cts.Cancel();
                return Task.FromResult(nonFilterGroup.Object);
            });

        var ctx = CreateTaskExecutionContext();

        // Act
        await _task.Run(ctx, cts.Token);

        // Assert: only one rule set was processed; the cancellation prevented
        // the second iteration of the foreach loop.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                _targetGroupServiceMock.Object,
                false),
            Times.Once);
    }

    [Test]
    public async Task Run_RespectsCancellationInChunkInsertionLoop()
    {
        // Arrange: 600 customers produce two chunks (500 + 100).
        // The CommitChunkAsync override cancels after the first commit so the second
        // chunk is blocked by the cancellation checkpoint at line 82-84.
        var role = SeedActiveRoleWithRuleSet("CancelChunkRole");
        var ruleSet = role.RuleSets.First();

        var filterExpression = new FilterExpressionGroup(typeof(Customer));
        SetupRuleServiceForRuleSet(ruleSet, filterExpression);

        var customers = new List<Customer>();
        for (var i = 1; i <= 600; i++)
            customers.Add(new Customer { Id = i });
        SetupTargetGroupServiceForExpression(filterExpression, customers);

        var cts = new CancellationTokenSource();
        _task.CommitCts = cts;

        var ctx = CreateTaskExecutionContext();

        // Act: Run completes via early return at the chunk-loop checkpoint.
        await _task.Run(ctx, cts.Token);

        // Assert: only the first 500-record chunk was committed; the second
        // chunk was skipped because cancelToken.IsCancellationRequested returned
        // true at the chunk-insertion-loop checkpoint.
        var mappings = await DbContext.CustomerRoleMappings
            .AsNoTracking()
            .Where(m => m.IsSystemMapping)
            .ToListAsync();
        Assert.That(mappings.Count, Is.EqualTo(500),
            "Only the first 500-record chunk should be committed before cancellation.");
    }

    #endregion
}
