using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>
/// Shared base class for TargetGroupEvaluatorTask test classes.
/// Provides common SQLite-backed test infrastructure, mock setup, and helper methods.
/// </summary>
public abstract class TargetGroupEvaluatorTaskTestBase : ServiceTestBase
{
    protected SqliteConnection _sqliteConnection;
    protected SmartDbContext _sqliteDb;

    protected Mock<IRuleService> _ruleServiceMock;
    protected Mock<ITargetGroupService> _targetGroupServiceMock;
    protected Mock<IRuleProviderFactory> _ruleProviderFactoryMock;
    protected Mock<ICacheManager> _cacheMock;
    protected Mock<ITaskStore> _taskStoreMock;
    protected Mock<IAsyncState> _asyncStateMock;

    protected TargetGroupEvaluatorTask _sut;

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

    protected TaskExecutionContext CreateTaskExecutionContext(IDictionary<string, string> taskParameters = null)
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
    /// Seeds a customer role with associated rule sets into the database.
    /// Returns the CustomerRole entity after it has been saved.
    /// </summary>
    protected CustomerRole SeedRoleWithRuleSets(string systemName, int ruleSetCount, bool roleActive = true, bool ruleSetActive = true)
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
    protected List<Customer> SeedCustomers(int count)
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
    protected void SeedSystemMappings(int roleId, IEnumerable<int> customerIds)
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
    /// Seeds manual (non-system) mappings into the database.
    /// </summary>
    protected void SeedManualMappings(int roleId, IEnumerable<int> customerIds)
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

    /// <summary>
    /// Creates a mock IPagedList backed by a real DbContext queryable so FastPager can iterate.
    /// </summary>
    protected IPagedList<Customer> CreatePagedListFromQuery(IQueryable<Customer> sourceQuery)
    {
        var pagedListMock = new Mock<IPagedList<Customer>>();
        pagedListMock.Setup(x => x.SourceQuery).Returns(sourceQuery);
        return pagedListMock.Object;
    }

    /// <summary>
    /// Sets up the rule service mock to return a FilterExpression for a given rule set.
    /// </summary>
    protected void SetupRuleServiceReturnsFilterExpression(RuleSetEntity ruleSet)
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
    protected void SetupTargetGroupServiceReturnsCustomers(IEnumerable<int> customerIds)
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
    protected void SetupTargetGroupServiceReturnsEmpty()
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
}
