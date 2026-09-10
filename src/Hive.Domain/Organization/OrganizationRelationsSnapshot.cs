using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Organization;

/// <summary>
/// Immutable, materialized read model of the organizational relations of a single organization,
/// holding exactly the structure that vertical routing and governance validation (US-F0-04)
/// query: the leadership of each unit, the configured <c>OrganizationOwner</c>, the unit each
/// position belongs to, and the direct superior of each position. Direct subordinates are derived
/// from the superior edges so the two views can never disagree.
/// </summary>
/// <remarks>
/// <para>
/// This is the "registry materializado" the read-only service of US-F0-04-T02 serves over. In F0
/// the snapshot is held in memory and built directly; the GitOps/YAML import of US-F0-05 and the
/// PostgreSQL-backed materialization are responsible for populating it later. Building a snapshot
/// is the only place that touches the structure — every query is a pure lookup.
/// </para>
/// <para>
/// The builder enforces the internal invariants the read model must always hold regardless of its
/// source: every position belongs to a unit, every declared superior references a known position,
/// exactly one position (the root unit leadership) has no superior, and the superior edges form a
/// tree without cycles. Each unit has one declared leader belonging to it; the root unit leader
/// can be derived from the command-tree root. Cross-file YAML validation belongs to US-F0-05.
/// </para>
/// </remarks>
public sealed class OrganizationRelationsSnapshot
{
    private readonly IReadOnlyDictionary<PositionId, PositionId?> _superiorByPosition;
    private readonly IReadOnlyDictionary<PositionId, UnitId> _unitByPosition;
    private readonly IReadOnlyDictionary<UnitId, PositionId> _leadershipByUnit;
    private readonly IReadOnlyDictionary<PositionId, IReadOnlyCollection<PositionId>> _subordinatesByPosition;

    private OrganizationRelationsSnapshot(
        OrganizationId organizationId,
        OrganizationOwnerEndpointRef owner,
        PositionId rootUnitLeadership,
        IReadOnlyDictionary<PositionId, PositionId?> superiorByPosition,
        IReadOnlyDictionary<PositionId, UnitId> unitByPosition,
        IReadOnlyDictionary<PositionId, IReadOnlyCollection<PositionId>> subordinatesByPosition,
        IReadOnlyDictionary<UnitId, PositionId> leadershipByUnit)
    {
        OrganizationId = organizationId;
        Owner = owner;
        RootUnitLeadership = rootUnitLeadership;
        _superiorByPosition = superiorByPosition;
        _unitByPosition = unitByPosition;
        _subordinatesByPosition = subordinatesByPosition;
        _leadershipByUnit = leadershipByUnit;
    }

    /// <summary>The organization this snapshot describes. Every query is scoped to it.</summary>
    public OrganizationId OrganizationId { get; }

    /// <summary>
    /// The routing endpoint of the configured <c>OrganizationOwner</c>, destination of escalations
    /// raised by the root unit leadership and of the kill switch. Mandatory per organization.
    /// </summary>
    public OrganizationOwnerEndpointRef Owner { get; }

    /// <summary>The single position that leads the root unit and has no direct superior.</summary>
    public PositionId RootUnitLeadership { get; }

    /// <summary>Returns the declared leader, throwing a structural error for an unknown unit.</summary>
    public PositionId GetUnitLeadership(UnitId unitId)
    {
        ArgumentNullException.ThrowIfNull(unitId);
        return _leadershipByUnit.TryGetValue(unitId, out var leader)
            ? leader
            : throw OrganizationRelationNotFoundException.ForUnit(OrganizationId, unitId);
    }

    /// <summary>
    /// Starts building a snapshot for <paramref name="organizationId"/> with the mandatory
    /// <paramref name="owner"/> routing endpoint.
    /// </summary>
    public static Builder CreateBuilder(OrganizationId organizationId, OrganizationOwnerEndpointRef owner)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(owner);

        return new Builder(organizationId, owner);
    }

    /// <summary>Whether <paramref name="positionId"/> exists in this organization.</summary>
    public bool ContainsPosition(PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(positionId);

        return _unitByPosition.ContainsKey(positionId);
    }

    /// <summary>
    /// Returns the direct superior of a known <paramref name="positionId"/>, or <see langword="null"/>
    /// when it is the root unit leadership. The caller must ensure the position exists.
    /// </summary>
    public PositionId? GetDirectSuperior(PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(positionId);

        return _superiorByPosition[positionId];
    }

    /// <summary>
    /// Returns the direct subordinates of a known <paramref name="positionId"/> in declaration
    /// order, empty for a leaf. The caller must ensure the position exists.
    /// </summary>
    public IReadOnlyCollection<PositionId> GetDirectSubordinates(PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(positionId);

        return _subordinatesByPosition[positionId];
    }

    /// <summary>
    /// Returns the unit of <paramref name="positionId"/>, or <see langword="null"/> when the
    /// position does not exist. Doubles as the existence probe.
    /// </summary>
    public UnitId? GetUnit(PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(positionId);

        return _unitByPosition.TryGetValue(positionId, out var unit) ? unit : null;
    }

    /// <summary>
    /// Accumulates positions and leadership declarations, validating the read model invariants
    /// when <see cref="Build"/> is called.
    /// </summary>
    public sealed class Builder
    {
        private readonly OrganizationId _organizationId;
        private readonly OrganizationOwnerEndpointRef _owner;
        private readonly List<PositionId> _order = new();
        private readonly Dictionary<PositionId, PositionId?> _superiorByPosition = new();
        private readonly Dictionary<PositionId, UnitId> _unitByPosition = new();
        private readonly Dictionary<UnitId, PositionId> _leadershipByUnit = new();

        /// <summary>Declares a unit's leader. Positions may be added before or after this call.</summary>
        public Builder AddUnitLeadership(UnitId unitId, PositionId leadership)
        {
            ArgumentNullException.ThrowIfNull(unitId);
            ArgumentNullException.ThrowIfNull(leadership);
            if (!_leadershipByUnit.TryAdd(unitId, leadership))
            {
                throw new ArgumentException($"Unit '{unitId.Value}' already has a declared leader.", nameof(unitId));
            }

            return this;
        }

        internal Builder(OrganizationId organizationId, OrganizationOwnerEndpointRef owner)
        {
            _organizationId = organizationId;
            _owner = owner;
        }

        /// <summary>
        /// Adds a position belonging to <paramref name="unit"/> with an optional
        /// <paramref name="directSuperior"/> (<see langword="null"/> for the root unit leadership).
        /// </summary>
        /// <exception cref="ArgumentException">The position was already added.</exception>
        public Builder AddPosition(PositionId positionId, UnitId unit, PositionId? directSuperior = null)
        {
            ArgumentNullException.ThrowIfNull(positionId);
            ArgumentNullException.ThrowIfNull(unit);

            if (_unitByPosition.ContainsKey(positionId))
            {
                throw new ArgumentException(
                    $"Position '{positionId.Value}' was already added to the snapshot.",
                    nameof(positionId));
            }

            if (directSuperior is not null && directSuperior == positionId)
            {
                throw new ArgumentException(
                    $"Position '{positionId.Value}' cannot be its own direct superior.",
                    nameof(directSuperior));
            }

            _order.Add(positionId);
            _unitByPosition[positionId] = unit;
            _superiorByPosition[positionId] = directSuperior;
            return this;
        }

        /// <summary>
        /// Validates the accumulated structure and produces an immutable snapshot.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The structure is not a single rooted tree: it is empty, references an unknown superior,
        /// has zero or several positions without a superior, or contains a cycle. A unit is missing
        /// its leader, its leader is unknown or belongs elsewhere, or the root leader is inconsistent.
        /// </exception>
        public OrganizationRelationsSnapshot Build()
        {
            if (_order.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Organization '{_organizationId.Value}' must have at least the root unit leadership.");
            }

            PositionId? root = null;
            foreach (var position in _order)
            {
                var superior = _superiorByPosition[position];
                if (superior is null)
                {
                    if (root is not null)
                    {
                        throw new InvalidOperationException(
                            $"Organization '{_organizationId.Value}' has more than one position without a direct "
                            + $"superior ('{root.Value}' and '{position.Value}'); exactly one root unit leadership "
                            + "is expected.");
                    }

                    root = position;
                }
                else if (!_unitByPosition.ContainsKey(superior))
                {
                    throw new InvalidOperationException(
                        $"Position '{position.Value}' references an unknown direct superior "
                        + $"'{superior.Value}' in organization '{_organizationId.Value}'.");
                }
            }

            if (root is null)
            {
                throw new InvalidOperationException(
                    $"Organization '{_organizationId.Value}' has no root unit leadership: every position declares a "
                    + "direct superior, so the superior edges form a cycle.");
            }

            EnsureAcyclic(root);

            var leadershipByUnit = new Dictionary<UnitId, PositionId>(_leadershipByUnit);
            var rootUnit = _unitByPosition[root];
            leadershipByUnit.TryAdd(rootUnit, root);
            if (leadershipByUnit[rootUnit] != root)
            {
                throw new InvalidOperationException("The root unit leadership must match the command-tree root.");
            }

            foreach (var (unit, leader) in leadershipByUnit)
            {
                if (!_unitByPosition.TryGetValue(leader, out var leaderUnit) || leaderUnit != unit)
                {
                    throw new InvalidOperationException($"Unit '{unit.Value}' must have a known leader belonging to it.");
                }
            }

            foreach (var unit in _unitByPosition.Values.Distinct())
            {
                if (!leadershipByUnit.ContainsKey(unit))
                {
                    throw new InvalidOperationException($"Unit '{unit.Value}' requires a declared leader.");
                }
            }

            var subordinates = _order.ToDictionary(
                position => position,
                _ => (IReadOnlyCollection<PositionId>)new List<PositionId>());
            foreach (var position in _order)
            {
                var superior = _superiorByPosition[position];
                if (superior is not null)
                {
                    ((List<PositionId>)subordinates[superior]).Add(position);
                }
            }

            return new OrganizationRelationsSnapshot(
                _organizationId,
                _owner,
                root,
                new Dictionary<PositionId, PositionId?>(_superiorByPosition),
                new Dictionary<PositionId, UnitId>(_unitByPosition),
                subordinates,
                leadershipByUnit);
        }

        private void EnsureAcyclic(PositionId root)
        {
            foreach (var start in _order)
            {
                var current = start;
                var steps = 0;
                while (_superiorByPosition[current] is { } superior)
                {
                    current = superior;
                    if (++steps > _order.Count)
                    {
                        throw new InvalidOperationException(
                            $"The superior edges of organization '{_organizationId.Value}' form a cycle reachable "
                            + $"from position '{start.Value}'.");
                    }
                }

                if (current != root)
                {
                    throw new InvalidOperationException(
                        $"Position '{start.Value}' does not reach the root unit leadership '{root.Value}' "
                        + $"through its superior chain in organization '{_organizationId.Value}'.");
                }
            }
        }
    }
}
