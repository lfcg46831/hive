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
}
