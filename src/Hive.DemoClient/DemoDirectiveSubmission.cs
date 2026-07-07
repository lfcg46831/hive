namespace Hive.DemoClient;

public sealed record DemoDirectiveSubmission(
    string OrganizationId,
    string RelativePath,
    DemoDirectiveRequest Request);
