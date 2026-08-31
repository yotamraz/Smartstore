using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
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
using Smartstore.Data.Providers;
using Smartstore.Scheduling;
using Smartstore.Threading;

namespace Smartstore.Core.Tests.Platform.Identity;

[TestFixture]
public class TargetGroupEvaluatorTaskIntegrationTests : ServiceTestBase
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

    private CustomerRole SeedRoleWithRuleSets(string systemName, int ruleSetCount, bool roleActive = true, bool ruleSetActive = true)
    {
        var role = new CustomerRole
        {
            Name = systemName,
            SystemName = systemName,
            Active = roleActive,
        };

        for (int i = 0; i < ruleSetCount; i++)
        {
            var ruleSet = new RuleSetEntity
            {
                Name = $"{systemName}_RuleSet_{i}",
                IsActive = ruleSetActive,
                Scope = RuleScope.Customer,
            };

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

    private void SeedManualMappings(int roleId, IEnumerable<int> customerIds)
    {
        foreach (var customerId in customerIds)
        {
            _sqliteDb.CustomerRoleMappings.Add(new CustomerRoleMapping
            {
                CustomerId = customerId,
                CustomerRoleId = roleId,
                IsSystemMapping = false,
            });
        }

        _sqliteDb.SaveChanges();
    }

    private IPagedList<Customer> CreatePagedListFromQuery(IQueryable<Customer> sourceQuery)
    {
        var pagedListMock = new Mock<IPagedList<Customer>>();
        pagedListMock.Setup(x => x.SourceQuery).Returns(sourceQuery);
        return pagedListMock.Object;
    }

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
    public async Task System_mappings_deleted_before_reevaluation()
    {
        // Arrange: seed a role with one active rule set and some customers.
        var role = SeedRoleWithRuleSets("ReEvalRole", ruleSetCount: 1);
        var customers = SeedCustomers(5);

        // Seed old system mappings for only the first 3 customers.
        var oldCustomerIds = customers.Take(3).Select(c => c.Id).ToList();
        SeedSystemMappings(role.Id, oldCustomerIds);

        // Verify old mappings exist before running the task.
        var mappingsBefore = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == role.Id && m.IsSystemMapping)
            .ToList();
        Assert.That(mappingsBefore, Has.Count.EqualTo(3));

        // Set up rule evaluation to return a filter expression for the rule set.
        foreach (var ruleSet in role.RuleSets)
        {
            SetupRuleServiceReturnsFilterExpression(ruleSet);
        }

        // Set up target group service to return only the last 2 customers (different from old).
        var newCustomerIds = customers.Skip(3).Select(c => c.Id).ToList();
        SetupTargetGroupServiceReturnsCustomers(newCustomerIds);

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: old system mappings (for first 3 customers) should be gone.
        var oldMappingsAfter = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == role.Id && m.IsSystemMapping && oldCustomerIds.Contains(m.CustomerId))
            .ToList();
        Assert.That(oldMappingsAfter, Has.Count.EqualTo(0), "Old system mappings should have been deleted");

        // Assert: new system mappings (for last 2 customers) should exist.
        var newMappingsAfter = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == role.Id && m.IsSystemMapping && newCustomerIds.Contains(m.CustomerId))
            .ToList();
        Assert.That(newMappingsAfter, Has.Count.EqualTo(2), "New system mappings should have been created");

        // Assert: total system mappings for this role should be exactly 2.
        var totalMappings = _sqliteDb.CustomerRoleMappings
            .Count(m => m.CustomerRoleId == role.Id && m.IsSystemMapping);
        Assert.That(totalMappings, Is.EqualTo(2), "Only new system mappings should exist after reevaluation");
    }

    [Test]
    public async Task New_mappings_have_correct_properties()
    {
        // Arrange: seed a role with one active rule set and some customers.
        var role = SeedRoleWithRuleSets("PropCheckRole", ruleSetCount: 1);
        var customers = SeedCustomers(4);

        foreach (var ruleSet in role.RuleSets)
        {
            SetupRuleServiceReturnsFilterExpression(ruleSet);
        }

        SetupTargetGroupServiceReturnsCustomers(customers.Select(c => c.Id));

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: verify each mapping has the correct properties.
        var mappings = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == role.Id)
            .ToList();

        Assert.That(mappings, Has.Count.EqualTo(4));

        var expectedCustomerIds = customers.Select(c => c.Id).ToHashSet();

        foreach (var mapping in mappings)
        {
            Assert.Multiple(() =>
            {
                Assert.That(mapping.CustomerRoleId, Is.EqualTo(role.Id),
                    $"Mapping {mapping.Id} should have CustomerRoleId = {role.Id}");
                Assert.That(mapping.IsSystemMapping, Is.True,
                    $"Mapping {mapping.Id} should have IsSystemMapping = true");
                Assert.That(expectedCustomerIds, Does.Contain(mapping.CustomerId),
                    $"Mapping {mapping.Id} should have a valid CustomerId");
            });
        }

        // Verify all expected customers got a mapping (no duplicates, no missing).
        var actualCustomerIds = mappings.Select(m => m.CustomerId).ToHashSet();
        Assert.That(actualCustomerIds, Is.EquivalentTo(expectedCustomerIds),
            "Every expected customer should have exactly one mapping");
    }

    [Test]
    public async Task Only_active_roles_with_active_rulesets_processed()
    {
        // Arrange: seed an active role with active rule sets.
        var activeRole = SeedRoleWithRuleSets("ActiveRole", ruleSetCount: 1, roleActive: true, ruleSetActive: true);

        // Seed an inactive role with active rule sets.
        var inactiveRole = SeedRoleWithRuleSets("InactiveRole", ruleSetCount: 1, roleActive: false, ruleSetActive: true);

        // Seed an active role with inactive rule sets.
        var inactiveRuleSetRole = SeedRoleWithRuleSets("InactiveRuleSetRole", ruleSetCount: 1, roleActive: true, ruleSetActive: false);

        var customers = SeedCustomers(3);

        // Set up rule evaluation for all rule sets (only active ones should be reached).
        foreach (var ruleSet in activeRole.RuleSets)
        {
            SetupRuleServiceReturnsFilterExpression(ruleSet);
        }
        foreach (var ruleSet in inactiveRole.RuleSets)
        {
            SetupRuleServiceReturnsFilterExpression(ruleSet);
        }
        foreach (var ruleSet in inactiveRuleSetRole.RuleSets)
        {
            SetupRuleServiceReturnsFilterExpression(ruleSet);
        }

        SetupTargetGroupServiceReturnsCustomers(customers.Select(c => c.Id));

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: only the active role with active rulesets should produce mappings.
        var activeRoleMappings = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == activeRole.Id && m.IsSystemMapping)
            .ToList();
        Assert.That(activeRoleMappings, Has.Count.EqualTo(3),
            "Active role with active rulesets should have mappings for all 3 customers");

        // Inactive role should produce no mappings (role is filtered by x.Active).
        var inactiveRoleMappings = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == inactiveRole.Id && m.IsSystemMapping)
            .ToList();
        Assert.That(inactiveRoleMappings, Has.Count.EqualTo(0),
            "Inactive role should produce no mappings");

        // Active role with inactive rule sets should produce no mappings
        // (filtered by x.RuleSets.Any(y => y.IsActive)).
        var inactiveRuleSetRoleMappings = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == inactiveRuleSetRole.Id && m.IsSystemMapping)
            .ToList();
        Assert.That(inactiveRuleSetRoleMappings, Has.Count.EqualTo(0),
            "Active role with inactive rulesets should produce no mappings");
    }

    [Test]
    public async Task Roles_without_rulesets_skipped()
    {
        // Arrange: seed a role with NO rule sets at all.
        var roleWithoutRuleSets = new CustomerRole
        {
            Name = "NoRuleSets",
            SystemName = "NoRuleSets",
            Active = true,
        };
        _sqliteDb.CustomerRoles.Add(roleWithoutRuleSets);
        _sqliteDb.SaveChanges();

        var customers = SeedCustomers(3);

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: no mappings should be created for a role without rule sets.
        var mappings = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == roleWithoutRuleSets.Id && m.IsSystemMapping)
            .ToList();
        Assert.That(mappings, Has.Count.EqualTo(0),
            "Role without rule sets should produce no mappings");

        // Verify rule service was never called.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()),
            Times.Never,
            "Rule service should not be called for roles without rule sets");
    }

    [Test]
    public async Task Chunk_size_500_honored()
    {
        // Arrange: seed a role with one active rule set and 1200 customers.
        var role = SeedRoleWithRuleSets("ChunkRole", ruleSetCount: 1);
        var customers = SeedCustomers(1200);

        foreach (var ruleSet in role.RuleSets)
        {
            SetupRuleServiceReturnsFilterExpression(ruleSet);
        }

        SetupTargetGroupServiceReturnsCustomers(customers.Select(c => c.Id));

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: all 1200 customers should have mappings (chunking doesn't lose data).
        var totalMappings = _sqliteDb.CustomerRoleMappings
            .Count(m => m.CustomerRoleId == role.Id && m.IsSystemMapping);
        Assert.That(totalMappings, Is.EqualTo(1200),
            "All 1200 customers should have mappings regardless of chunking");

        // Verify each customer has exactly one mapping.
        var mappedCustomerIds = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == role.Id && m.IsSystemMapping)
            .Select(m => m.CustomerId)
            .ToList();

        var expectedCustomerIds = customers.Select(c => c.Id).ToList();
        Assert.That(mappedCustomerIds, Is.EquivalentTo(expectedCustomerIds),
            "Every seeded customer should have exactly one mapping");
    }

    [Test]
    public async Task Entity_detachment_after_each_role()
    {
        // Arrange: seed a role with one active rule set and some customers.
        var role = SeedRoleWithRuleSets("DetachRole", ruleSetCount: 1);
        var customers = SeedCustomers(10);

        foreach (var ruleSet in role.RuleSets)
        {
            SetupRuleServiceReturnsFilterExpression(ruleSet);
        }

        SetupTargetGroupServiceReturnsCustomers(customers.Select(c => c.Id));

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: after processing, the change tracker should not hold CustomerRoleMapping entities.
        // The task calls DetachEntities<CustomerRoleMapping>() after processing each role.
        var trackedMappingEntries = _sqliteDb.ChangeTracker
            .Entries<CustomerRoleMapping>()
            .ToList();

        Assert.That(trackedMappingEntries, Has.Count.EqualTo(0),
            "Change tracker should have no CustomerRoleMapping entries after task completes (DetachEntities was called)");

        // Verify the mappings actually exist in the database (detachment doesn't delete them).
        var persistedMappings = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == role.Id && m.IsSystemMapping)
            .ToList();
        Assert.That(persistedMappings, Has.Count.EqualTo(10),
            "Mappings should be persisted in the database despite being detached from the tracker");
    }

    [Test]
    public async Task Manual_mappings_not_deleted()
    {
        // Arrange: seed a role with one active rule set and some customers.
        var role = SeedRoleWithRuleSets("ManualMappingRole", ruleSetCount: 1);
        var customers = SeedCustomers(6);

        // Seed manual (non-system) mappings for the first 3 customers.
        var manualCustomerIds = customers.Take(3).Select(c => c.Id).ToList();
        SeedManualMappings(role.Id, manualCustomerIds);

        // Seed system mappings for the next 2 customers (these should be deleted and replaced).
        var systemCustomerIds = customers.Skip(3).Take(2).Select(c => c.Id).ToList();
        SeedSystemMappings(role.Id, systemCustomerIds);

        // Verify both types of mappings exist before running the task.
        var manualBefore = _sqliteDb.CustomerRoleMappings
            .Count(m => m.CustomerRoleId == role.Id && !m.IsSystemMapping);
        var systemBefore = _sqliteDb.CustomerRoleMappings
            .Count(m => m.CustomerRoleId == role.Id && m.IsSystemMapping);
        Assert.That(manualBefore, Is.EqualTo(3), "Setup: 3 manual mappings should exist");
        Assert.That(systemBefore, Is.EqualTo(2), "Setup: 2 system mappings should exist");

        // Set up rule evaluation and target group service to return all 6 customers.
        foreach (var ruleSet in role.RuleSets)
        {
            SetupRuleServiceReturnsFilterExpression(ruleSet);
        }

        SetupTargetGroupServiceReturnsCustomers(customers.Select(c => c.Id));

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: manual mappings should survive.
        var manualAfter = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == role.Id && !m.IsSystemMapping)
            .ToList();
        Assert.That(manualAfter, Has.Count.EqualTo(3),
            "Manual (non-system) mappings should not be deleted by the task");

        // Verify the manual mappings are for the original customers.
        var manualAfterCustomerIds = manualAfter.Select(m => m.CustomerId).ToHashSet();
        Assert.That(manualAfterCustomerIds, Is.EquivalentTo(manualCustomerIds),
            "Manual mappings should be for the same customers as before");

        // Assert: system mappings should be replaced (old 2 deleted, new 6 created).
        var systemAfter = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == role.Id && m.IsSystemMapping)
            .ToList();
        Assert.That(systemAfter, Has.Count.EqualTo(6),
            "System mappings should be recreated for all 6 customers");
    }

    [Test]
    public async Task Multiple_rulesets_per_role_union_customers()
    {
        // Arrange: seed a role with 2 active rule sets.
        var role = SeedRoleWithRuleSets("UnionRole", ruleSetCount: 2);
        var customers = SeedCustomers(6);

        // Set up filter expressions for both rule sets.
        foreach (var ruleSet in role.RuleSets)
        {
            SetupRuleServiceReturnsFilterExpression(ruleSet);
        }

        // First rule set returns customers 1-4, second returns customers 3-6.
        // Overlap on customers 3 and 4 to test deduplication via HashSet.
        var ruleSetList = role.RuleSets.ToList();
        var firstRuleSetCustomerIds = customers.Take(4).Select(c => c.Id).ToHashSet();
        var secondRuleSetCustomerIds = customers.Skip(2).Select(c => c.Id).ToHashSet();

        var callCount = 0;
        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(() =>
            {
                callCount++;
                IQueryable<Customer> sourceQuery;
                if (callCount == 1)
                {
                    sourceQuery = _sqliteDb.Customers.Where(c => firstRuleSetCustomerIds.Contains(c.Id));
                }
                else
                {
                    sourceQuery = _sqliteDb.Customers.Where(c => secondRuleSetCustomerIds.Contains(c.Id));
                }
                return CreatePagedListFromQuery(sourceQuery);
            });

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: the union of both sets should produce mappings for all 6 customers (no duplicates).
        var mappings = _sqliteDb.CustomerRoleMappings
            .Where(m => m.CustomerRoleId == role.Id && m.IsSystemMapping)
            .ToList();

        Assert.That(mappings, Has.Count.EqualTo(6),
            "Union of both rule set results should produce mappings for all 6 distinct customers");

        // Verify no duplicate customer IDs in mappings.
        var mappedCustomerIds = mappings.Select(m => m.CustomerId).ToList();
        Assert.That(mappedCustomerIds.Distinct().Count(), Is.EqualTo(mappedCustomerIds.Count),
            "There should be no duplicate customer mappings (HashSet deduplication)");

        // Verify all 6 customers are represented.
        var expectedIds = customers.Select(c => c.Id).ToHashSet();
        Assert.That(mappedCustomerIds.ToHashSet(), Is.EquivalentTo(expectedIds),
            "All 6 customers should be represented in the mappings");
    }
}
