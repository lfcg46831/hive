using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Hive.Domain.Identity;

namespace Hive.Domain.Outcomes;

public sealed record OutcomePolicyOverlay
{
    public OutcomePolicyOverlay(
        int? maximumIterations = null,
        int? maximumRetries = null,
        bool? verifierEnabled = null)
    {
        if (maximumIterations < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumIterations),
                maximumIterations,
                "Maximum iterations cannot be negative.");
        }

        if (maximumRetries < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRetries),
                maximumRetries,
                "Maximum retries cannot be negative.");
        }

        MaximumIterations = maximumIterations;
        MaximumRetries = maximumRetries;
        VerifierEnabled = verifierEnabled;
    }

    public int? MaximumIterations { get; }

    public int? MaximumRetries { get; }

    public bool? VerifierEnabled { get; }

    internal bool IsEmpty =>
        MaximumIterations is null && MaximumRetries is null && VerifierEnabled is null;
}

public static class OutcomeSystemPolicy
{
    public const string Version = "outcome-policy-v1";
    public const int MaximumIterations = 8;
    public const int MaximumRetries = 3;
    public const bool VerifierEnabled = true;

    public static ImmutableArray<OutcomePolicyTrigger> EscalationTriggers { get; } =
        Enum.GetValues<OutcomePolicyTrigger>().ToImmutableArray();
}

public static class OutcomePolicyComposer
{
    public static OutcomePolicySnapshot ComposeV1(
        long registryVersion,
        string registryFingerprint,
        OutcomePolicyOverlay? organizationOverlay,
        OutcomePolicyOverlay? positionOverlay,
        int? runtimeMaximumIterations = null)
    {
        if (registryVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(registryVersion),
                registryVersion,
                "Registry version must be positive.");
        }

        registryFingerprint = OutcomeContractGuards.RequireReference(
            registryFingerprint,
            nameof(registryFingerprint));
        if (runtimeMaximumIterations is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runtimeMaximumIterations),
                runtimeMaximumIterations,
                "Runtime maximum iterations must be positive when declared.");
        }

        var effective = new EffectivePolicy(
            OutcomeSystemPolicy.MaximumIterations,
            OutcomeSystemPolicy.MaximumRetries,
            OutcomeSystemPolicy.VerifierEnabled);
        effective = Tighten(effective, organizationOverlay, "organization");
        effective = Tighten(effective, positionOverlay, "position");
        if (runtimeMaximumIterations is { } runtimeLimit)
        {
            effective = effective with
            {
                MaximumIterations = Math.Min(effective.MaximumIterations, runtimeLimit),
            };
        }

        var version = $"{OutcomeSystemPolicy.Version}/registry-{registryVersion}";
        var fingerprint = Fingerprint(
            registryVersion,
            registryFingerprint,
            organizationOverlay,
            positionOverlay,
            runtimeMaximumIterations,
            effective);

        return new OutcomePolicySnapshot(
            version,
            fingerprint,
            effective.MaximumIterations,
            effective.MaximumRetries,
            effective.VerifierEnabled,
            OutcomeSystemPolicy.EscalationTriggers);
    }

    public static void RequireTighteningOverlay(
        OutcomePolicyOverlay? inherited,
        OutcomePolicyOverlay? candidate,
        string parameterName)
    {
        var effective = new EffectivePolicy(
            OutcomeSystemPolicy.MaximumIterations,
            OutcomeSystemPolicy.MaximumRetries,
            OutcomeSystemPolicy.VerifierEnabled);
        effective = Tighten(effective, inherited, "inherited");
        _ = Tighten(effective, candidate, parameterName);
    }

    private static EffectivePolicy Tighten(
        EffectivePolicy inherited,
        OutcomePolicyOverlay? overlay,
        string source)
    {
        if (overlay is null || overlay.IsEmpty)
        {
            return inherited;
        }

        if (overlay.MaximumIterations is { } maximumIterations &&
            maximumIterations > inherited.MaximumIterations)
        {
            throw new InvalidOperationException(
                $"The {source} outcome policy cannot increase maximum iterations from " +
                $"{inherited.MaximumIterations} to {maximumIterations}.");
        }

        if (overlay.MaximumRetries is { } maximumRetries &&
            maximumRetries > inherited.MaximumRetries)
        {
            throw new InvalidOperationException(
                $"The {source} outcome policy cannot increase maximum retries from " +
                $"{inherited.MaximumRetries} to {maximumRetries}.");
        }

        if (overlay.VerifierEnabled == true && !inherited.VerifierEnabled)
        {
            throw new InvalidOperationException(
                $"The {source} outcome policy cannot re-enable the verifier.");
        }

        return new EffectivePolicy(
            overlay.MaximumIterations ?? inherited.MaximumIterations,
            overlay.MaximumRetries ?? inherited.MaximumRetries,
            overlay.VerifierEnabled ?? inherited.VerifierEnabled);
    }

    private static string Fingerprint(
        long registryVersion,
        string registryFingerprint,
        OutcomePolicyOverlay? organizationOverlay,
        OutcomePolicyOverlay? positionOverlay,
        int? runtimeMaximumIterations,
        EffectivePolicy effective)
    {
        var canonical = string.Join(
            '\n',
            $"contract={OrganizationalOutcomeContractVersions.PolicySnapshot}",
            $"system={OutcomeSystemPolicy.Version}",
            $"registry-version={registryVersion}",
            $"registry-fingerprint={registryFingerprint}",
            $"organization={Canonical(organizationOverlay)}",
            $"position={Canonical(positionOverlay)}",
            $"runtime-max-iterations={Canonical(runtimeMaximumIterations)}",
            $"effective-max-iterations={effective.MaximumIterations}",
            $"effective-max-retries={effective.MaximumRetries}",
            $"effective-verifier-enabled={Canonical(effective.VerifierEnabled)}",
            $"triggers={string.Join(',', OutcomeSystemPolicy.EscalationTriggers.Order())}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string Canonical(OutcomePolicyOverlay? overlay) =>
        overlay is null
            ? "none"
            : string.Join(
                ',',
                Canonical(overlay.MaximumIterations),
                Canonical(overlay.MaximumRetries),
                overlay.VerifierEnabled is { } enabled ? Canonical(enabled) : "null");

    private static string Canonical(int? value) => value?.ToString() ?? "null";

    private static string Canonical(bool value) => value ? "true" : "false";

    private sealed record EffectivePolicy(
        int MaximumIterations,
        int MaximumRetries,
        bool VerifierEnabled);
}

public interface IOutcomePolicyProvider
{
    ValueTask<OutcomePolicySnapshot> GetPolicyAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default);
}

public sealed record OrganizationalOutcomeContext(
    ExecutionFacts Facts,
    DirectiveExecutionContract Directive,
    OutcomePolicySnapshot Policy);

/// <summary>
/// Shared composition seam used by every organizational function before invoking the resolver.
/// </summary>
public sealed class OrganizationalOutcomeContextComposer
{
    private readonly IExecutionFactsMaterializer _factsMaterializer;
    private readonly IOutcomePolicyProvider _policyProvider;

    public OrganizationalOutcomeContextComposer(
        IExecutionFactsMaterializer factsMaterializer,
        IOutcomePolicyProvider policyProvider)
    {
        _factsMaterializer = factsMaterializer
            ?? throw new ArgumentNullException(nameof(factsMaterializer));
        _policyProvider = policyProvider
            ?? throw new ArgumentNullException(nameof(policyProvider));
    }

    public async ValueTask<OrganizationalOutcomeContext> ComposeAsync(
        OrganizationId organizationId,
        PositionId positionId,
        OutcomeRuntimeSnapshot runtime,
        DirectiveExecutionContract directive,
        CancellationToken cancellationToken = default)
        => await ComposeCoreAsync(
            organizationId,
            positionId,
            runtime,
            directive,
            proposal: null,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<OrganizationalOutcomeContext> ComposeAsync(
        OrganizationId organizationId,
        PositionId positionId,
        OutcomeRuntimeSnapshot runtime,
        DirectiveExecutionContract directive,
        OutcomeProposal proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return await ComposeCoreAsync(
            organizationId,
            positionId,
            runtime,
            directive,
            proposal,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<OrganizationalOutcomeContext> ComposeCoreAsync(
        OrganizationId organizationId,
        PositionId positionId,
        OutcomeRuntimeSnapshot runtime,
        DirectiveExecutionContract directive,
        OutcomeProposal? proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(directive);

        var facts = _factsMaterializer.Materialize(runtime, directive, proposal);
        var policy = await _policyProvider
            .GetPolicyAsync(organizationId, positionId, cancellationToken)
            .ConfigureAwait(false);
        return new OrganizationalOutcomeContext(facts, directive, policy);
    }
}
