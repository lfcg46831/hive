using System.Collections.Immutable;
using Hive.Domain.Governance;
using Hive.Infrastructure.Connectors.Email;
using Hive.Infrastructure.Connectors.Jira;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Infrastructure.Governance;

public interface IActionDomainContractSource
{
    IReadOnlyList<ActionDomainActionContract> ActionContracts { get; }

    IReadOnlyList<ActionAttributeExtractorRegistration> ActionExtractors { get; }
}

public interface IActionDomainContractRegistry
{
    IReadOnlyList<ActionDomainActionContract> ActionContracts { get; }

    IReadOnlyList<ActionAttributeExtractorRegistration> ActionExtractors { get; }
}

public sealed class ActionDomainContractRegistry : IActionDomainContractRegistry
{
    public ActionDomainContractRegistry(IEnumerable<IActionDomainContractSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var sourceSnapshot = sources.ToArray();
        if (sourceSnapshot.Any(source => source is null))
        {
            throw new ArgumentException("Contract sources cannot contain null entries.", nameof(sources));
        }

        ActionContracts = sourceSnapshot
            .SelectMany(source => source.ActionContracts)
            .OrderBy(contract => contract.Action)
            .ThenBy(contract => contract.SelectorValue, StringComparer.Ordinal)
            .ToImmutableArray();
        ActionExtractors = sourceSnapshot
            .SelectMany(source => source.ActionExtractors)
            .OrderBy(extractor => extractor.Action)
            .ThenBy(extractor => extractor.SelectorValue, StringComparer.Ordinal)
            .ToImmutableArray();

        Validate();
    }

    public IReadOnlyList<ActionDomainActionContract> ActionContracts { get; }

    public IReadOnlyList<ActionAttributeExtractorRegistration> ActionExtractors { get; }

    private void Validate()
    {
        var contracts = ActionContracts
            .GroupBy(contract => (contract.Action, contract.SelectorValue))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var duplicateContract = contracts.FirstOrDefault(pair => pair.Value.Length != 1);
        if (duplicateContract.Value is not null)
        {
            throw new InvalidOperationException(
                $"Action contract '{duplicateContract.Key.Action}:{duplicateContract.Key.SelectorValue}' is registered more than once.");
        }

        var extractors = ActionExtractors
            .GroupBy(extractor => (extractor.Action, extractor.SelectorValue))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var duplicateExtractor = extractors.FirstOrDefault(pair => pair.Value.Length != 1);
        if (duplicateExtractor.Value is not null)
        {
            throw new InvalidOperationException(
                $"Action extractor '{duplicateExtractor.Key.Action}:{duplicateExtractor.Key.SelectorValue}' is registered more than once.");
        }

        foreach (var (key, registeredContracts) in contracts)
        {
            var hasExtractor = extractors.ContainsKey(key);
            if (registeredContracts[0].HasDerivedAttributes != hasExtractor)
            {
                throw new InvalidOperationException(
                    $"Action contract '{key.Action}:{key.SelectorValue}' has an invalid extractor registration.");
            }
        }

        var orphanExtractor = extractors.Keys.FirstOrDefault(key => !contracts.ContainsKey(key));
        if (orphanExtractor != default)
        {
            throw new InvalidOperationException(
                $"Action extractor '{orphanExtractor.Action}:{orphanExtractor.SelectorValue}' has no registered contract.");
        }
    }
}

public static class ActionDomainContractServiceCollectionExtensions
{
    public static IServiceCollection AddHiveActionDomainContracts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IActionDomainContractSource, PlatformMessageActionDomainContractSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IActionDomainContractSource, JiraActionDomainContractSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IActionDomainContractSource, EmailActionDomainContractSource>());
        services.TryAddSingleton<IActionDomainContractRegistry, ActionDomainContractRegistry>();
        return services;
    }
}
