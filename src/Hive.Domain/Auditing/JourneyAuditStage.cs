namespace Hive.Domain.Auditing;

public enum JourneyAuditStage
{
    SubmissionReceived = 1,
    DirectiveCreated = 2,
    PositionAccepted = 3,
    PositionDispatched = 4,
    GatewayCalled = 5,
    AgentDecided = 6,
    ResultMessageCreated = 7,
    GatewayCostRecorded = 8,
    DuplicateSuppressed = 9,
    ActionGateEvaluated = 10,
    RetainedActionResume = 11,
    AuthorizationResolution = 12,
    RetainedActionLifecycle = 13,
    RetainedActionReEscalation = 14,
}
