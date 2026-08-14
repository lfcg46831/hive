using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Infrastructure.Connectors;

public static partial class ConnectorPluginServiceCollectionExtensions
{
    public const string AssembliesSectionName = "Hive:Connectors:Plugins:Assemblies";

    /// <summary>
    /// Loads only explicitly configured connector assemblies, discovers their generic plugin
    /// entry points and lets each plugin own its service and options registration.
    /// </summary>
    public static IServiceCollection AddHiveConnectorPlugins(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var assemblyNames = configuration
            .GetSection(AssembliesSectionName)
            .Get<string[]>() ?? [];
        ValidateAssemblyNames(assemblyNames);

        var plugins = assemblyNames
            .SelectMany(DiscoverPlugins)
            .ToArray();
        ValidatePluginIds(plugins);

        foreach (var plugin in plugins)
        {
            services.AddSingleton(typeof(IConnectorPlugin), plugin.Instance);
        }

        services.TryAddSingleton<IConnectorToolRegistry, ConnectorToolRegistry>();

        services.AddSingleton(new ConnectorPluginCatalog(plugins.Select(plugin =>
            new ConnectorPluginDescriptor(
                plugin.Instance.Id,
                plugin.AssemblyName,
                plugin.TypeName))));

        foreach (var plugin in plugins)
        {
            plugin.Instance.ConfigureServices(services, configuration);
        }

        return services;
    }

    private static IEnumerable<DiscoveredPlugin> DiscoverPlugins(string assemblyName)
    {
        Assembly assembly;
        try
        {
            assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.GetName().Name,
                        assemblyName,
                        StringComparison.OrdinalIgnoreCase))
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(
                    AppContext.BaseDirectory,
                    assemblyName + ".dll"));
        }
        catch (Exception exception)
            when (exception is FileNotFoundException
                or FileLoadException
                or BadImageFormatException)
        {
            throw new InvalidOperationException(
                $"Connector plugin assembly '{assemblyName}' could not be loaded.",
                exception);
        }

        Type[] exportedTypes;
        try
        {
            exportedTypes = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            throw new InvalidOperationException(
                $"Connector plugin assembly '{assemblyName}' could not be inspected.",
                exception);
        }

        var pluginTypes = exportedTypes
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && typeof(IConnectorPlugin).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        if (pluginTypes.Length == 0)
        {
            throw new InvalidOperationException(
                $"Connector plugin assembly '{assemblyName}' exposes no {nameof(IConnectorPlugin)} implementation.");
        }

        foreach (var pluginType in pluginTypes)
        {
            if (pluginType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidOperationException(
                    $"Connector plugin entry point '{pluginType.FullName}' must expose a public parameterless constructor.");
            }

            IConnectorPlugin plugin;
            try
            {
                plugin = (IConnectorPlugin)Activator.CreateInstance(pluginType)!;
            }
            catch (Exception exception)
                when (exception is TargetInvocationException
                    or MemberAccessException
                    or MissingMethodException)
            {
                throw new InvalidOperationException(
                    $"Connector plugin entry point '{pluginType.FullName}' could not be activated.",
                    exception);
            }

            yield return new DiscoveredPlugin(
                assembly.GetName().Name ?? assemblyName,
                pluginType.FullName ?? pluginType.Name,
                plugin);
        }
    }

    private static void ValidateAssemblyNames(IReadOnlyList<string> assemblyNames)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < assemblyNames.Count; index++)
        {
            var assemblyName = assemblyNames[index];
            if (string.IsNullOrWhiteSpace(assemblyName)
                || !string.Equals(assemblyName, assemblyName.Trim(), StringComparison.Ordinal)
                || !AssemblyNamePattern().IsMatch(assemblyName))
            {
                throw new InvalidOperationException(
                    $"{AssembliesSectionName}:{index} must be an assembly simple name, not a path or display name.");
            }

            if (!seen.Add(assemblyName))
            {
                throw new InvalidOperationException(
                    $"Connector plugin assembly '{assemblyName}' is configured more than once.");
            }
        }
    }

    private static void ValidatePluginIds(IReadOnlyList<DiscoveredPlugin> plugins)
    {
        var duplicate = plugins
            .GroupBy(plugin => plugin.Instance.Id?.Value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Key is null || group.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(duplicate.Key is null
                ? "A connector plugin returned no connector id."
                : $"Connector plugin id '{duplicate.Key}' is declared more than once.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AssemblyNamePattern();

    private sealed record DiscoveredPlugin(
        string AssemblyName,
        string TypeName,
        IConnectorPlugin Instance);
}
