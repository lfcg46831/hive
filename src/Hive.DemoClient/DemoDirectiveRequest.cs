namespace Hive.DemoClient;

public sealed record DemoDirectiveRequest(
    string MessageId,
    DemoDirectiveEndpointRef From,
    DemoDirectiveEndpointRef To,
    string ThreadId,
    string Priority,
    int SchemaVersion,
    DateTimeOffset SentAt,
    DateTimeOffset? Deadline,
    string DirectiveId,
    string? ParentDirectiveId,
    string Objective,
    string Context);
