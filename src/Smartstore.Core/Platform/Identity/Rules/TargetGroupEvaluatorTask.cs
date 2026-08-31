using Microsoft.Extensions.Logging;
using Smartstore.Caching;
using Smartstore.Core.Data;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Core.Security;
using Smartstore.Data;
using Smartstore.Data.Hooks;
using Smartstore.Scheduling;
using Smartstore.Utilities;

using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Smartstore.Core.Identity.Rules;

public partial class TargetGroupEvaluatorTask(
    SmartDbContext db,
    ICacheManager cache,
    IRuleService ruleService,
    IRuleProviderFactory ruleProviderFactory) : ITask
{
    private static readonly Action<ILogger, string, bool, Exception> _logRunStarted =
        LoggerMessage.Define<string, bool>(
            MsLogLevel.Debug, 0,
            "TargetGroupEvaluatorTask.Run started. CustomerRoleIds={CustomerRoleIds}, CancellationRequested={CancellationRequested}");

    private static readonly Action<ILogger, int, bool, Exception> _logBulkDeleted =
        LoggerMessage.Define<int, bool>(
            MsLogLevel.Debug, 0,
            "Deleted {NumDeleted} system mappings. ScopedToRoleIds={ScopedToRoleIds}");

    private static readonly Action<ILogger, int, string, int, Exception> _logProcessingRole =
        LoggerMessage.Define<int, string, int>(
            MsLogLevel.Debug, 0,
            "Processing role {RoleId} \"{RoleName}\" with {RuleSetCount} active rule sets");

    private static readonly Action<ILogger, int, string, int, Exception> _logRuleEvaluationResult =
        LoggerMessage.Define<int, string, int>(
            MsLogLevel.Debug, 0,
            "Rule set {RuleSetId} evaluated: ExpressionType={ExpressionType}, MatchingCustomerIds={MatchingCustomerCount}");

    private static readonly Action<ILogger, int, int, int, Exception> _logChunkInserted =
        LoggerMessage.Define<int, int, int>(
            MsLogLevel.Debug, 0,
            "Inserted chunk {ChunkIndex}: {ChunkSize} mappings ({TotalInserted} total so far)");

    private static readonly Action<ILogger, int, string, Exception> _logEntityDetachment =
        LoggerMessage.Define<int, string>(
            MsLogLevel.Debug, 0,
            "Detached CustomerRoleMapping entities for role {RoleId} \"{RoleName}\"");

    private static readonly Action<ILogger, bool, int, int, Exception> _logCacheInvalidation =
        LoggerMessage.Define<bool, int, int>(
            MsLogLevel.Debug, 0,
            "Cache invalidation: Cleared={Cleared}, NumAdded={NumAdded}, NumDeleted={NumDeleted}");

    private static readonly Action<ILogger, int, int, int, long, Exception> _logRunCompleted =
        LoggerMessage.Define<int, int, int, long>(
            MsLogLevel.Debug, 0,
            "TargetGroupEvaluatorTask.Run completed. RolesProcessed={RolesProcessed}, MappingsCreated={MappingsCreated}, MappingsDeleted={MappingsDeleted}, ElapsedMs={ElapsedMs}");

    protected readonly SmartDbContext _db = db;
    protected readonly ICacheManager _cache = cache;
    protected readonly IRuleService _ruleService = ruleService;
    protected readonly ITargetGroupService _targetGroupService = ruleProviderFactory.GetProvider<ITargetGroupService>(RuleScope.Customer);

    public ILogger Logger { get; set; } = NullLogger.Instance;

    public async Task Run(TaskExecutionContext ctx, CancellationToken cancelToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var count = 0;
        var numDeleted = 0;
        var numAdded = 0;
        var rolesCount = 0;

        var hasRoleFilter = ctx.Parameters.ContainsKey("CustomerRoleIds");
        int[] roleIds = hasRoleFilter ? ctx.Parameters["CustomerRoleIds"].ToIntArray() : null;

        _logRunStarted(Logger, roleIds != null ? string.Join(",", roleIds) : "(none)", cancelToken.IsCancellationRequested, null);

        using (var scope = new DbContextScope(_db, autoDetectChanges: false, minHookImportance: HookImportance.Important, deferCommit: true))
        {
            // Delete existing system mappings.
            var deleteQuery = _db.CustomerRoleMappings.Where(x => x.IsSystemMapping);

            if (hasRoleFilter)
            {
                deleteQuery = deleteQuery.Where(x => roleIds.Contains(x.CustomerRoleId));
            }

            numDeleted = await deleteQuery.ExecuteDeleteAsync(cancelToken);

            _logBulkDeleted(Logger, numDeleted, hasRoleFilter, null);

            // Insert new customer role mappings.
            var rolesQuery = _db.CustomerRoles
                .Include(x => x.RuleSets)
                .ThenInclude(x => x.Rules)
                .AsNoTracking()
                .AsSplitQuery()
                .Where(x => x.Active && x.RuleSets.Any(y => y.IsActive));

            if (hasRoleFilter)
            {
                rolesQuery = rolesQuery.Where(x => roleIds.Contains(x.Id));
            }

            var roles = await rolesQuery.ToListAsync(cancelToken);
            rolesCount = roles.Count;

            foreach (var role in roles)
            {
                var ruleSetCustomerIds = new HashSet<int>();
                var activeRuleSets = role.RuleSets.Where(x => x.IsActive).ToList();

                _logProcessingRole(Logger, role.Id, role.SystemName.NaIfEmpty(), activeRuleSets.Count, null);

                await ctx.SetProgressAsync(++count, roles.Count, $"Add customer assignments for role \"{role.SystemName.NaIfEmpty()}\".");

                // Execute active rule sets and collect customer ids.
                foreach (var ruleSet in activeRuleSets)
                {
                    if (cancelToken.IsCancellationRequested)
                        return;

                    var countBefore = ruleSetCustomerIds.Count;

                    var expressionGroup = await _ruleService.CreateExpressionGroupAsync(ruleSet, _targetGroupService);
                    if (expressionGroup is FilterExpression expression)
                    {
                        var filterResult = _targetGroupService.ProcessFilter(expression, 0, 500);
                        var resultPager = new FastPager<Customer>(filterResult.SourceQuery, 500);

                        while ((await resultPager.ReadNextPageAsync(x => x.Id, x => x, cancelToken)).Out(out var customerIds))
                        {
                            ruleSetCustomerIds.AddRange(customerIds);
                        }
                    }

                    _logRuleEvaluationResult(Logger, ruleSet.Id, expressionGroup?.GetType().Name ?? "null", ruleSetCustomerIds.Count - countBefore, null);
                }

                // Add mappings.
                if (ruleSetCustomerIds.Any())
                {
                    var chunkIndex = 0;
                    foreach (var chunk in ruleSetCustomerIds.Chunk(500))
                    {
                        if (cancelToken.IsCancellationRequested)
                            return;

                        foreach (var customerId in chunk)
                        {
                            _db.CustomerRoleMappings.Add(new CustomerRoleMapping
                            {
                                CustomerId = customerId,
                                CustomerRoleId = role.Id,
                                IsSystemMapping = true
                            });

                            ++numAdded;
                        }

                        await scope.CommitAsync(cancelToken);

                        _logChunkInserted(Logger, chunkIndex, chunk.Length, numAdded, null);
                        chunkIndex++;
                    }

                    try
                    {
                        scope.DbContext.DetachEntities<CustomerRoleMapping>();
                        _logEntityDetachment(Logger, role.Id, role.SystemName.NaIfEmpty(), null);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug(ex, "DetachEntities failed for role {RoleId} \"{RoleName}\"", role.Id, role.SystemName);
                    }
                }
            }
        }

        var cacheCleared = numAdded > 0 || numDeleted > 0;
        if (cacheCleared)
        {
            await _cache.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN);
        }

        _logCacheInvalidation(Logger, cacheCleared, numAdded, numDeleted, null);

        stopwatch.Stop();
        _logRunCompleted(Logger, rolesCount, numAdded, numDeleted, stopwatch.ElapsedMilliseconds, null);
    }
}
