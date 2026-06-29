# US-F0-07-T02 AI Gateway Service Design

## Context

`US-F0-07-T02` creates the first executable AI gateway boundary. The project bible, version 0.93, fixes the decision: the boundary is a DI service named `IAiGateway`, not an actor. The service consumes the provider-neutral contracts already defined in `Hive.Domain.Ai` by `US-F0-07-T01` and hides `Microsoft.Extensions.AI` plus concrete provider SDKs from HIVE agents and domain code.

The `AiGatewayActor` remains out of scope for this task. If it becomes necessary, it will be a later thin wrapper over `IAiGateway` for `US-F0-07-T12` or `US-F0-08`, when mailbox, backpressure, supervision, provider queues, or cluster distribution are needed.

## Goals

- Add a stable asynchronous port, `IAiGateway`, expressed only in HIVE terms.
- Add a minimal executable service implementation outside `Hive.Domain`.
- Keep `Hive.Domain` free of `Microsoft.Extensions.AI`, Akka, hosting, DI, provider SDKs, HTTP clients, secrets, and configuration binding.
- Register the gateway in the common bootstrap so hosts can resolve it through DI.
- Prove the service boundary with focused unit and composition tests.

## Non-Goals

- Do not implement `AiGatewayActor`.
- Do not resolve position AI configuration from the registry. That belongs to `US-F0-07-T03`.
- Do not implement the deterministic provider stub. That belongs to `US-F0-07-T04`.
- Do not implement a real provider adapter, secrets, or network calls. That belongs to `US-F0-07-T05`.
- Do not normalize provider request or response shapes beyond the existing HIVE DTOs. That belongs to `US-F0-07-T06` and `US-F0-07-T07`.
- Do not apply provider/model/tool authorization, budget, timeout policy, retries, fallback, cost measurement, or audit envelopes. Those belong to `US-F0-07-T08` through `US-F0-07-T11`.

## Architecture

`Hive.Domain.Ai` owns the port:

```csharp
public interface IAiGateway
{
    Task<AiGatewayResponse> CompleteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default);
}
```

The interface lives with the provider-neutral request and response records so callers can depend on the semantic HIVE boundary without taking a dependency on infrastructure. It exposes no SDK-specific types and makes cancellation part of the call contract.

`Hive.Infrastructure.Ai` owns the first executable implementation. The implementation is deliberately thin: it validates the request argument, delegates the call to a provider adapter seam, and returns the resulting `AiGatewayResponse`. The adapter seam also uses only HIVE contracts in this task, so later tasks can plug in the deterministic stub or a `Microsoft.Extensions.AI` adapter without changing callers.

The common bootstrap registers the gateway and its initial unavailable adapter with `TryAddSingleton`-style wiring, so tests and future provider tasks can replace the implementation without modifying host startup.

## Data Flow

1. A caller builds an `AiGatewayRequest` with `OrganizationId`, `PositionId`, `ThreadId`, `MessageId`, content, optional context, tools, model parameters, and metadata.
2. The caller invokes `IAiGateway.CompleteAsync(request, cancellationToken)`.
3. `AiGateway` rejects a null request before invoking any adapter.
4. `AiGateway` delegates to the configured adapter seam.
5. The adapter returns a valid `AiGatewayResponse`.
6. The response is returned unchanged to the caller.

The initial unavailable adapter returns a structured `AiGatewayResponse.Failed(...)` with `AiGatewayErrorCode.ConfigurationInvalid` and a sanitized message. This gives the boundary executable without pretending that provider configuration, stub scenarios, or real network calls exist in T02.

## Error Handling

Confirmed absence of an adapter/provider is represented as a structured failure response, not an exception. Null programmer errors remain exceptions. Cancellation is propagated as cancellation when the token is already canceled or when the adapter observes cancellation; this task does not convert cancellation into `AiGatewayErrorCode.Canceled` because timeout/cancellation normalization is assigned to `US-F0-07-T09`.

Provider-specific exceptions, quota errors, invalid provider payloads, retries, and timeout mapping are not implemented in this task.

## Testing

Tests cover the boundary rather than provider behavior:

- `IAiGateway` exists in `Hive.Domain.Ai` and uses only `AiGatewayRequest`, `AiGatewayResponse`, `Task`, and `CancellationToken`.
- `Hive.Domain` continues to reject `Microsoft.Extensions.AI`, provider SDK, hosting, DI, and Akka references.
- The infrastructure gateway rejects null requests.
- The infrastructure gateway delegates exactly one call to its adapter and returns the adapter response unchanged.
- Cancellation reaches the adapter.
- The common bootstrap registers `IAiGateway` and resolves the infrastructure implementation without requiring network, secrets, or provider packages.

## Acceptance

`US-F0-07-T02` is complete when host composition can resolve an executable `IAiGateway`, callers only see the HIVE contract, the domain remains SDK-free, no actor wrapper has been introduced, and all tests pass without external network access or provider credentials.
