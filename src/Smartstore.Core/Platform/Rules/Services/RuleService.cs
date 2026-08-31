using System.Globalization;
using Autofac;
using Smartstore.Core.Data;
using Smartstore.Core.Localization;
using Smartstore.Core.Rules.Rendering;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Smartstore.Core.Rules;

public partial class RuleService : IRuleService
{
    private static readonly Action<ILogger, int, string, bool, Exception> _logLoadingRuleSet =
        LoggerMessage.Define<int, string, bool>(MsLogLevel.Debug, 0,
            "Loading rule set {RuleSetId} for expression group (Scope={Scope}, IncludeHidden={IncludeHidden})");

    private static readonly Action<ILogger, int, Exception> _logRuleSetNotFound =
        LoggerMessage.Define<int>(MsLogLevel.Debug, 0,
            "Rule set {RuleSetId} not found");

    private static readonly Action<ILogger, int, int, bool, Exception> _logRuleSetLoaded =
        LoggerMessage.Define<int, int, bool>(MsLogLevel.Debug, 0,
            "Rule set {RuleSetId} loaded with {RuleCount} rules, IsActive={IsActive}");

    private static readonly Action<ILogger, string, string, int, Exception> _logResolvingProvider =
        LoggerMessage.Define<string, string, int>(MsLogLevel.Debug, 0,
            "Resolving rule provider {ProviderType} for scope {Scope} on rule set {RuleSetId}");

    private static readonly Action<ILogger, int, Exception> _logSkippingInactiveRuleSet =
        LoggerMessage.Define<int>(MsLogLevel.Debug, 0,
            "Skipping inactive rule set {RuleSetId}");

    private static readonly Action<ILogger, int, int, string, Exception> _logBuiltExpressionGroup =
        LoggerMessage.Define<int, int, string>(MsLogLevel.Debug, 0,
            "Built expression group for rule set {RuleSetId}: {ExpressionCount} expressions, Operator={LogicalOperator}");

    private static readonly Action<ILogger, int, string, string, Exception> _logVisitingRule =
        LoggerMessage.Define<int, string, string>(MsLogLevel.Debug, 0,
            "Visiting rule {RuleId}: Type={RuleType}, Operator={Operator}");

    private static readonly Action<ILogger, int, string, Exception> _logVisitingSubGroup =
        LoggerMessage.Define<int, string>(MsLogLevel.Debug, 0,
            "Visiting sub-group rule {RuleId}, target RuleSetId={SubGroupRuleSetId}");

    private readonly SmartDbContext _db;
    private readonly IWorkContext _workContext;
    private readonly Lazy<IEnumerable<IRuleOptionsProvider>> _ruleOptionsProviders;

    public RuleService(
        SmartDbContext db,
        IWorkContext workContext,
        Lazy<IEnumerable<IRuleOptionsProvider>> ruleOptionsProviders)
    {
        _db = db;
        _workContext = workContext;
        _ruleOptionsProviders = ruleOptionsProviders;
    }

    public ILogger Logger { get; set; } = NullLogger.Instance;

    public Localizer T { get; set; } = NullLocalizer.Instance;

    public virtual async Task<bool> ApplyRuleSetMappingsAsync<T>(T entity, int[] selectedRuleSetIds)
        where T : BaseEntity, IRulesContainer
    {
        Guard.NotNull(entity);

        selectedRuleSetIds ??= Array.Empty<int>();

        await _db.LoadCollectionAsync(entity, x => x.RuleSets);

        if (!selectedRuleSetIds.Any() && !entity.RuleSets.Any())
        {
            // Nothing to do.
            return false;
        }

        var updated = false;
        var allRuleSets = await _db.RuleSets
            .AsQueryable() // Prevent ambiguous extension method call.
            .Where(x => !x.IsSubGroup)
            .ToDictionaryAsync(x => x.Id);

        foreach (var ruleSetId in allRuleSets.Keys)
        {
            if (selectedRuleSetIds.Contains(ruleSetId))
            {
                if (!entity.RuleSets.Any(x => x.Id == ruleSetId))
                {
                    entity.RuleSets.Add(allRuleSets[ruleSetId]);
                    updated = true;
                }
            }
            else if (entity.RuleSets.Any(x => x.Id == ruleSetId))
            {
                entity.RuleSets.Remove(allRuleSets[ruleSetId]);
                updated = true;
            }
        }

        return updated;
    }

    public async Task<IRuleExpressionGroup> CreateExpressionGroupAsync(int ruleSetId, IRuleVisitor visitor, bool includeHidden = false)
    {
        if (ruleSetId <= 0)
        {
            return null;
        }

        _logLoadingRuleSet(Logger, ruleSetId, visitor.Scope.ToString(), includeHidden, null);

        // TODO: prevent stack overflow > check if nested groups reference each other.

        var ruleSet = await _db.RuleSets
            .AsNoTracking()
            .Include(x => x.Rules)
            .FirstOrDefaultAsync(x => x.Id == ruleSetId);

        if (ruleSet == null)
        {
            _logRuleSetNotFound(Logger, ruleSetId, null);
            // TODO: ErrHandling (???)
            return null;
        }

        _logRuleSetLoaded(Logger, ruleSetId, ruleSet.Rules.Count, ruleSet.IsActive, null);

        return await CreateExpressionGroupAsync(ruleSet, visitor, includeHidden);
    }

    public virtual async Task<IRuleExpressionGroup> CreateExpressionGroupAsync(RuleSetEntity ruleSet, IRuleVisitor visitor, bool includeHidden = false)
    {
        _logResolvingProvider(Logger, visitor.GetType().Name, visitor.Scope.ToString(), ruleSet.Id, null);

        if (ruleSet.Scope != visitor.Scope)
        {
            throw new InvalidOperationException($"Differing rule scope {ruleSet.Scope}. Expected {visitor.Scope}.");
        }

        if (!includeHidden && !ruleSet.IsActive)
        {
            _logSkippingInactiveRuleSet(Logger, ruleSet.Id, null);
            return null;
        }

        await _db.LoadCollectionAsync(ruleSet, x => x.Rules);

        var group = visitor.VisitRuleSet(ruleSet);

        var expressions = await ruleSet.Rules
            .SelectAwait(x => CreateExpression(x, visitor))
            .Where(x => x != null)
            .ToArrayAsync();

        group.AddExpressions(expressions);

        _logBuiltExpressionGroup(Logger, ruleSet.Id, expressions.Length, ruleSet.LogicalOperator.ToString(), null);

        return group;
    }

    public virtual async Task<int> ApplyRuleDataAsync(RuleEditItem[] ruleData, IRuleProvider provider)
    {
        Guard.NotNull(ruleData);
        Guard.NotNull(provider);

        var ruleIds = ruleData?.Select(x => x.RuleId)?.Distinct()?.ToArray();
        if (ruleIds.IsNullOrEmpty())
        {
            return 0;
        }

        var rules = await _db.Rules
            .Where(x => ruleIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);
        if (rules.Count == 0)
        {
            return 0;
        }

        var num = 0;
        var descriptors = await provider.GetRuleDescriptorsAsync();

        foreach (var data in ruleData)
        {
            if (rules.TryGetValue(data.RuleId, out var entity))
            {
                if (data.Value.HasValue())
                {
                    var descriptor = descriptors.FindDescriptor(entity.RuleType);

                    if (data.Op == RuleOperator.IsEmpty || data.Op == RuleOperator.IsNotEmpty ||
                        data.Op == RuleOperator.IsNull || data.Op == RuleOperator.IsNotNull)
                    {
                        data.Value = null;
                    }
                    else if (descriptor.RuleType == RuleType.DateTime || descriptor.RuleType == RuleType.NullableDateTime)
                    {
                        // Always store invariant formatted UTC values, otherwise database queries return inaccurate results.
                        var dt = data.Value.Convert<DateTime>(CultureInfo.CurrentCulture).ToUniversalTime();
                        data.Value = dt.ToString(CultureInfo.InvariantCulture);
                    }
                }

                entity.Operator = data.Op;
                entity.Value = data.Value;
                ++num;
            }
        }

        return num;
    }

    public virtual Task ApplyMetadataAsync(IRuleExpressionGroup group, Language language = null)
    {
        return ApplyMetadataInternal(group, language ?? _workContext.WorkingLanguage, group.Id);
    }

    private async Task ApplyMetadataInternal(IRuleExpressionGroup group, Language language, int rootRuleSetId)
    {
        if (group == null)
        {
            return;
        }

        foreach (var expression in group.Expressions)
        {
            if (expression is IRuleExpressionGroup subGroup)
            {
                await ApplyMetadataInternal(subGroup, language, rootRuleSetId);
                continue;
            }

            if (!expression.Descriptor.IsValid)
            {
                expression.Metadata["Error"] = T("Admin.Rules.InvalidDescriptor").Value;
            }

            // Load name and subtitle (e.g. SKU) for selected options.
            if (expression.Descriptor.SelectList is RemoteRuleValueSelectList list)
            {
                var optionsProvider = _ruleOptionsProviders.Value.FirstOrDefault(x => x.Matches(list.DataSource));
                if (optionsProvider != null)
                {
                    var options = await optionsProvider.GetOptionsAsync(new RuleOptionsContext(RuleOptionsRequestReason.SelectedDisplayNames, expression)
                    {
                        PageSize = int.MaxValue,
                        Language = language
                    });

                    expression.Metadata["SelectedItems"] = options.Options.ToDictionarySafe(
                        x => x.Value,
                        x => new RuleSelectItem { Text = x.Text, Hint = x.Hint });

                    expression.Metadata["RootRuleSetId"] = rootRuleSetId;
                }
            }
        }
    }

    //private async Task<IRuleExpressionGroup> CreateExpressionGroup(RuleSetEntity ruleSet, RuleEntity refRule, IRuleVisitor visitor)
    //{
    //    if (ruleSet.Scope != visitor.Scope)
    //    {
    //        // TODO: ErrHandling (ruleSet is for a different scope)
    //        return null;
    //    }

    //    await _db.LoadCollectionAsync(ruleSet, x => x.Rules);

    //    var group = visitor.VisitRuleSet(ruleSet);
    //    if (refRule != null)
    //    {
    //        group.RefRuleId = refRule.Id;
    //    }

    //    var expressions = await ruleSet.Rules
    //        .SelectAwait(x => CreateExpression(x, visitor))
    //        .Where(x => x != null)
    //        .ToArrayAsync();

    //    group.AddExpressions(expressions);

    //    return group;
    //}

    private async Task<IRuleExpression> CreateExpression(RuleEntity ruleEntity, IRuleVisitor visitor)
    {
        if (!ruleEntity.IsGroup)
        {
            _logVisitingRule(Logger, ruleEntity.Id, ruleEntity.RuleType, ruleEntity.Operator.ToString(), null);

            return await visitor.VisitRuleAsync(ruleEntity);
        }

        // It's a group, do recursive call.
        _logVisitingSubGroup(Logger, ruleEntity.Id, ruleEntity.Value, null);

        var group = await CreateExpressionGroupAsync(ruleEntity.Value.Convert<int>(), visitor);
        if (group != null)
        {
            group.RefRuleId = ruleEntity.Id;
        }

        return group;
    }
}