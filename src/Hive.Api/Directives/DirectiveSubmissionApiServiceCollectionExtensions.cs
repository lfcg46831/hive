namespace Hive.Api.Directives;

public static class DirectiveSubmissionApiServiceCollectionExtensions
{
    public static IServiceCollection AddHiveDirectiveSubmissionApi(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IDirectiveSubmissionSink, AcceptedDirectiveSubmissionSink>();
        return services;
    }
}
