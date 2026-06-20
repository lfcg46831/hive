using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hive.Infrastructure.Hosting;

/// <summary>
/// Starts exactly the workloads whose role this node declares, and nothing else.
/// This is the role-conditional activation seam for the minimal host (US-F0-01-T06):
/// it owns the composition decision, while each <see cref="IRoleWorkload"/> owns its own
/// activation mechanism. In F0 the host registers no production workloads yet; later stories
/// add real ones (e.g. Cluster Sharding for <c>agents</c>) without changing this service.
/// </summary>
public sealed class RoleWorkloadHostedService : IHostedService
{
    private readonly IReadOnlyList<IRoleWorkload> _workloads;
    private readonly ActiveNodeRoles _activeRoles;
    private readonly ILogger<RoleWorkloadHostedService> _logger;
    private readonly List<IRoleWorkload> _started = new();

    public RoleWorkloadHostedService(
        IEnumerable<IRoleWorkload> workloads,
        ActiveNodeRoles activeRoles,
        ILogger<RoleWorkloadHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(workloads);
        _workloads = workloads.ToArray();
        _activeRoles = activeRoles ?? throw new ArgumentNullException(nameof(activeRoles));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Workloads that were matched to an active role and started, in start order.</summary>
    public IReadOnlyList<IRoleWorkload> StartedWorkloads => _started;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var workload in _workloads)
        {
            if (!_activeRoles.Contains(workload.Role))
            {
                _logger.LogDebug(
                    "Skipping workload {Workload}: role {Role} is not active on this node.",
                    workload.GetType().Name,
                    workload.Role);
                continue;
            }

            _logger.LogInformation(
                "Starting workload {Workload} for role {Role}.",
                workload.GetType().Name,
                workload.Role);
            await workload.StartAsync(cancellationToken).ConfigureAwait(false);
            _started.Add(workload);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        for (var index = _started.Count - 1; index >= 0; index--)
        {
            await _started[index].StopAsync(cancellationToken).ConfigureAwait(false);
        }

        _started.Clear();
    }
}
