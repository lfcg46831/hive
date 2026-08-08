using System.Globalization;
using Hive.Api.Authorization;
using Hive.Contracts.Inbox;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Inbox.ReadModels;
using Microsoft.AspNetCore.Mvc;

namespace Hive.Api.Inbox;

public static class InboxEndpointExtensions
{
    public const string BasePath = "/api/v1/organizations";

    public const string InboxRoute = "/{organizationId}/inbox";

    public const string PositionInboxRoute = "/{organizationId}/positions/{positionId}/inbox";

    public const string InboxItemRoute = "/{organizationId}/inbox/{itemId}";

    public static IEndpointRouteBuilder MapHiveInboxApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(BasePath)
            .WithTags("Inbox")
            .RequireAuthorization(OrganizationAuthorizationDefaults.Policy);
        group.MapGet(InboxRoute, ListInboxAsync)
            .WithName("GetOrganizationInboxV1")
            .WithSummary("List the authenticated person's organization inbox")
            .WithDescription(
                "Returns a cursor-paginated, server-filtered inbox snapshot across the authenticated " +
                "person's occupied positions. The fixed order is deadline, priority, message timestamp " +
                "and stable item identifier.")
            .Produces<InboxPage>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet(PositionInboxRoute, ListPositionInboxAsync)
            .WithName("GetOrganizationPositionInboxV1")
            .WithSummary("List one occupied position's inbox")
            .WithDescription(
                "Returns the authenticated person's inbox subset for one occupied position, with the " +
                "same filters, pagination and fixed ordering as the aggregate inbox.")
            .Produces<InboxPage>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet(InboxItemRoute, ReadInboxItemAsync)
            .WithName("GetOrganizationInboxItemV1")
            .WithSummary("Get one inbox item")
            .WithDescription(
                "Returns one principal-scoped inbox item with thread correlation, deadline, response " +
                "state and approval metadata.")
            .Produces<InboxItemResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapPost(InboxItemRoute + "/read", MarkInboxItemReadAsync)
            .WithName("MarkOrganizationInboxItemReadV1")
            .WithSummary("Mark one inbox item as read")
            .WithDescription(
                "Persists and audits person-scoped read state without emitting an organizational message.")
            .Produces<InboxInteractionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapPost(InboxItemRoute + "/unread", MarkInboxItemUnreadAsync)
            .WithName("MarkOrganizationInboxItemUnreadV1")
            .WithSummary("Mark one inbox item as unread")
            .WithDescription(
                "Persists and audits person-scoped unread state without emitting an organizational message.")
            .Produces<InboxInteractionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapPost(InboxItemRoute + "/draft", SaveInboxItemDraftAsync)
            .WithName("SaveOrganizationInboxItemDraftV1")
            .WithSummary("Start or draft one inbox response")
            .WithDescription(
                "A null body starts a response, text saves or replaces the principal's single " +
                "plain-text draft, and an empty body clears that draft. No organizational message is emitted.")
            .Accepts<InboxDraftRequest>("application/json")
            .Produces<InboxInteractionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapPost(InboxItemRoute + "/reply", ReplyToInboxItemAsync)
            .WithName("ReplyToOrganizationInboxItemV1")
            .WithSummary("Reply to one inbox item as its occupied position")
            .WithDescription(
                "Converts authenticated human input into the closed canonical response mapping " +
                "inside the occupied PositionActor, preserving thread correlation and authorship audit.")
            .Accepts<InboxReplyRequest>("application/json")
            .Produces<InboxReplyResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapPost(InboxItemRoute + "/decision", DecideInboxApprovalAsync)
            .WithName("DecideOrganizationInboxApprovalV1")
            .WithSummary("Approve or reject one pending human approval request")
            .WithDescription(
                "Resolves the principal-scoped ApprovalRequest through its occupied PositionActor, " +
                "which emits a canonical, correlated and audited ApprovalDecision after governance validation.")
            .Accepts<InboxDecisionRequest>("application/json")
            .Produces<InboxDecisionResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static Task<IResult> ListInboxAsync(
        string organizationId,
        [AsParameters] InboxQueryParameters parameters,
        IInboxReadModel readModel,
        IOrganizationPrincipalResolver principalResolver,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        ListInboxCoreAsync(
            organizationId,
            positionId: null,
            parameters,
            readModel,
            principalResolver,
            httpContext,
            cancellationToken);

    private static Task<IResult> ListPositionInboxAsync(
        string organizationId,
        string positionId,
        [AsParameters] InboxQueryParameters parameters,
        IInboxReadModel readModel,
        IOrganizationPrincipalResolver principalResolver,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        ListInboxCoreAsync(
            organizationId,
            positionId,
            parameters,
            readModel,
            principalResolver,
            httpContext,
            cancellationToken);

    private static async Task<IResult> ListInboxCoreAsync(
        string organizationId,
        string? positionId,
        InboxQueryParameters parameters,
        IInboxReadModel readModel,
        IOrganizationPrincipalResolver principalResolver,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var organization))
        {
            return InvalidOrganizationId();
        }

        PositionId? position = null;
        if (positionId is not null && !TryParsePositionId(positionId, out position))
        {
            return InvalidPositionId();
        }

        if (!TryCreateQuery(parameters, out var query, out var error))
        {
            return InvalidQuery(error!);
        }

        var scope = await ResolveScopeAsync(
                organization!,
                principalResolver,
                httpContext,
                cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return OrganizationNotFound();
        }

        if (position is not null && !scope.Occupies(position))
        {
            return PositionNotFound();
        }

        var result = await readModel.ListAsync(
                scope,
                position,
                query!,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsAvailable)
        {
            return ReadModelUnavailable();
        }

        if (result.Value is not { } page)
        {
            return position is null ? OrganizationNotFound() : PositionNotFound();
        }

        return TypedResults.Ok(page);
    }

    private static async Task<IResult> ReadInboxItemAsync(
        string organizationId,
        string itemId,
        IInboxReadModel readModel,
        IOrganizationPrincipalResolver principalResolver,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var organization))
        {
            return InvalidOrganizationId();
        }

        if (!TryParseItemId(itemId, out var parsedItemId))
        {
            return InvalidItemId();
        }

        var scope = await ResolveScopeAsync(
                organization!,
                principalResolver,
                httpContext,
                cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return OrganizationNotFound();
        }

        var result = await readModel.ReadItemAsync(
                scope,
                parsedItemId!,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsAvailable)
        {
            return ReadModelUnavailable();
        }

        return result.Value is { } item
            ? TypedResults.Ok(item)
            : InboxItemNotFound();
    }

    private static async Task<IResult> ReplyToInboxItemAsync(
        string organizationId,
        string itemId,
        InboxReplyRequest? request,
        IInboxReadModel readModel,
        IInboxReplyCommandSink replySink,
        IOrganizationPrincipalResolver principalResolver,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var organization))
        {
            return InvalidOrganizationId();
        }

        if (!TryParseItemId(itemId, out var parsedItemId))
        {
            return InvalidItemId();
        }

        if (!TryValidateReplyRequest(request, out var body, out var requestedReportKind, out var problem))
        {
            return problem!;
        }

        var scope = await ResolveScopeAsync(
                organization!,
                principalResolver,
                httpContext,
                cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return OrganizationNotFound();
        }

        var readResult = await readModel.ReadItemAsync(
                scope,
                parsedItemId!,
                cancellationToken)
            .ConfigureAwait(false);
        if (!readResult.IsAvailable)
        {
            return ReadModelUnavailable();
        }

        if (readResult.Value?.Item is not { } item)
        {
            return InboxItemNotFound();
        }

        if (!TryResolveReplyMetadata(
                item.Type,
                requestedReportKind,
                out var reportKind,
                out var replyDirectiveId,
                out problem))
        {
            return problem!;
        }

        if (!replySink.IsAvailable)
        {
            return ReplyEmissionUnavailable();
        }

        var command = new EmitOccupantReply(
            MessageId.From(item.MessageId),
            MessageId.New(),
            OccupantReplyAuthor.HumanUser(scope.PersonId, "web-inbox"),
            body!,
            reportKind,
            replyDirectiveId);
        OccupantReplyEmissionResult emission;
        try
        {
            emission = await replySink.EmitAsync(
                    PositionEntityId.From(
                        organization!,
                        PositionId.From(item.AssignedPositionId)),
                    command,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InboxReplyEmissionUnavailableException)
        {
            return ReplyEmissionUnavailable();
        }

        if (!emission.IsAccepted)
        {
            return ReplyRejected(emission);
        }

        return TypedResults.Json(
            ToReplyResponse(emission),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> DecideInboxApprovalAsync(
        string organizationId,
        string itemId,
        InboxDecisionRequest? request,
        IInboxReadModel readModel,
        IInboxDecisionCommandSink decisionSink,
        IOrganizationPrincipalResolver principalResolver,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var organization))
        {
            return InvalidOrganizationId();
        }

        if (!TryParseItemId(itemId, out var parsedItemId))
        {
            return InvalidItemId();
        }

        if (!TryValidateDecisionRequest(request, out var approved, out var reason, out var problem))
        {
            return problem!;
        }

        var scope = await ResolveScopeAsync(
                organization!,
                principalResolver,
                httpContext,
                cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return OrganizationNotFound();
        }

        var readResult = await readModel.ReadItemAsync(
                scope,
                parsedItemId!,
                cancellationToken)
            .ConfigureAwait(false);
        if (!readResult.IsAvailable)
        {
            return ReadModelUnavailable();
        }

        if (readResult.Value?.Item is not { } item)
        {
            return InboxItemNotFound();
        }

        if (item.Type != InboxMessageType.ApprovalRequest || item.Approval is null)
        {
            return InvalidDecision(
                "Only an ApprovalRequest can be decided.",
                "item_id");
        }

        if (item.Origin.Type != InboxMessageEndpointType.Position || item.Origin.PositionId is null)
        {
            return InvalidDecision(
                "The ApprovalRequest requester must be a position.",
                "item_id");
        }

        if (!decisionSink.IsAvailable)
        {
            return DecisionEmissionUnavailable();
        }

        var command = new EmitOccupantApprovalDecision(
            MessageId.From(item.Approval.RequestId),
            MessageId.New(),
            Hive.Domain.Identity.ThreadId.From(item.ThreadId),
            PositionId.From(item.Origin.PositionId),
            MapPriority(item.Priority),
            OccupantReplyAuthor.HumanUser(scope.PersonId, "web-inbox"),
            approved,
            reason);
        OccupantReplyEmissionResult emission;
        try
        {
            emission = await decisionSink.EmitAsync(
                    PositionEntityId.From(
                        organization!,
                        PositionId.From(item.AssignedPositionId)),
                    command,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InboxDecisionEmissionUnavailableException)
        {
            return DecisionEmissionUnavailable();
        }

        if (!emission.IsAccepted)
        {
            return DecisionRejected(emission);
        }

        return TypedResults.Json(
            ToDecisionResponse(emission),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static Task<IResult> MarkInboxItemReadAsync(
        string organizationId,
        string itemId,
        IInboxReadModel readModel,
        IInboxInteractionCommandSink interactionSink,
        IOrganizationPrincipalResolver principalResolver,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        ApplyInteractionAsync(
            organizationId,
            itemId,
            InboxInteractionAction.MarkRead,
            draftText: null,
            readModel,
            interactionSink,
            principalResolver,
            timeProvider,
            httpContext,
            cancellationToken);

    private static Task<IResult> MarkInboxItemUnreadAsync(
        string organizationId,
        string itemId,
        IInboxReadModel readModel,
        IInboxInteractionCommandSink interactionSink,
        IOrganizationPrincipalResolver principalResolver,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        ApplyInteractionAsync(
            organizationId,
            itemId,
            InboxInteractionAction.MarkUnread,
            draftText: null,
            readModel,
            interactionSink,
            principalResolver,
            timeProvider,
            httpContext,
            cancellationToken);

    private static Task<IResult> SaveInboxItemDraftAsync(
        string organizationId,
        string itemId,
        InboxDraftRequest? request,
        IInboxReadModel readModel,
        IInboxInteractionCommandSink interactionSink,
        IOrganizationPrincipalResolver principalResolver,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Task.FromResult(InvalidDraft("Request body is required.", "body"));
        }

        if (request.Body?.Length > EmitOccupantReply.MaximumBodyLength)
        {
            return Task.FromResult(InvalidDraft(
                $"body cannot exceed {EmitOccupantReply.MaximumBodyLength} characters.",
                "body"));
        }

        var action = request.Body switch
        {
            null => InboxInteractionAction.StartReply,
            "" => InboxInteractionAction.ClearDraft,
            _ => InboxInteractionAction.SaveDraft,
        };
        return ApplyInteractionAsync(
            organizationId,
            itemId,
            action,
            request.Body is { Length: > 0 } ? request.Body : null,
            readModel,
            interactionSink,
            principalResolver,
            timeProvider,
            httpContext,
            cancellationToken);
    }

    private static async Task<IResult> ApplyInteractionAsync(
        string organizationId,
        string itemId,
        InboxInteractionAction action,
        string? draftText,
        IInboxReadModel readModel,
        IInboxInteractionCommandSink interactionSink,
        IOrganizationPrincipalResolver principalResolver,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var organization))
        {
            return InvalidOrganizationId();
        }

        if (!TryParseItemId(itemId, out var parsedItemId))
        {
            return InvalidItemId();
        }

        var scope = await ResolveScopeAsync(
                organization!,
                principalResolver,
                httpContext,
                cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return OrganizationNotFound();
        }

        var readResult = await readModel.ReadItemAsync(
                scope,
                parsedItemId!,
                cancellationToken)
            .ConfigureAwait(false);
        if (!readResult.IsAvailable)
        {
            return ReadModelUnavailable();
        }

        if (readResult.Value?.Item is not { } item)
        {
            return InboxItemNotFound();
        }

        if (!interactionSink.IsAvailable)
        {
            return InteractionStoreUnavailable();
        }

        var occurredAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var state = await interactionSink.ApplyAsync(
                new InboxInteractionMutation(
                    new InboxProjectionItemKey(
                        organization!,
                        PositionId.From(item.AssignedPositionId),
                        MessageId.From(item.MessageId)),
                    scope.PersonId,
                    action,
                    occurredAtUtc,
                    draftText),
                cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(new InboxInteractionResponse(
            occurredAtUtc,
            readResult.Value.LastEventAppliedAtUtc,
            item.ItemId,
            MapInteractionEnum<InboxInteractionReadState, InboxReadState>(state.ReadState),
            InteractionResponseState(item.ResponseState, state.ReplyState),
            state.DraftText,
            state.UpdatedAtUtc));
    }

    private static InboxResponseState InteractionResponseState(
        InboxResponseState derivedState,
        InboxInteractionReplyState interactionState) =>
        derivedState == InboxResponseState.AwaitingResponse &&
        interactionState == InboxInteractionReplyState.InProgress
            ? InboxResponseState.InProgress
            : derivedState;

    private static TTarget MapInteractionEnum<TSource, TTarget>(TSource value)
        where TSource : struct, Enum
        where TTarget : struct, Enum =>
        Enum.TryParse<TTarget>(value.ToString(), ignoreCase: false, out var mapped) &&
        Enum.IsDefined(mapped)
            ? mapped
            : throw new InvalidOperationException(
                $"Inbox interaction value '{typeof(TSource).Name}.{value}' has no public mapping.");

    private static bool TryValidateDecisionRequest(
        InboxDecisionRequest? request,
        out bool approved,
        out string? reason,
        out IResult? problem)
    {
        approved = default;
        reason = null;
        problem = null;
        if (request?.Approved is not { } decision)
        {
            problem = InvalidDecision("approved is required.", "approved");
            return false;
        }

        if (request.Reason is { } suppliedReason
            && (string.IsNullOrWhiteSpace(suppliedReason)
                || !string.Equals(
                    suppliedReason,
                    suppliedReason.Trim(),
                    StringComparison.Ordinal)))
        {
            problem = InvalidDecision(
                "reason must be omitted or contain text without leading or trailing whitespace.",
                "reason");
            return false;
        }

        if (request.Reason?.Length > EmitOccupantApprovalDecision.MaximumReasonLength)
        {
            problem = InvalidDecision(
                $"reason cannot exceed {EmitOccupantApprovalDecision.MaximumReasonLength} characters.",
                "reason");
            return false;
        }

        approved = decision;
        reason = request.Reason;
        return true;
    }

    private static bool TryValidateReplyRequest(
        InboxReplyRequest? request,
        out string? body,
        out string? reportKind,
        out IResult? problem)
    {
        body = null;
        reportKind = null;
        problem = null;
        if (request is null)
        {
            problem = InvalidReply("Request body is required.", "body");
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Body)
            || !string.Equals(request.Body, request.Body.Trim(), StringComparison.Ordinal))
        {
            problem = InvalidReply(
                "body must contain text without leading or trailing whitespace.",
                "body");
            return false;
        }

        if (request.Body.Length > EmitOccupantReply.MaximumBodyLength)
        {
            problem = InvalidReply(
                $"body cannot exceed {EmitOccupantReply.MaximumBodyLength} characters.",
                "body");
            return false;
        }

        if (request.ReportKind is not null
            && (string.IsNullOrWhiteSpace(request.ReportKind)
                || !string.Equals(
                    request.ReportKind,
                    request.ReportKind.Trim(),
                    StringComparison.Ordinal)))
        {
            problem = InvalidReply(
                "report_kind must be omitted or contain a supported value without surrounding whitespace.",
                "report_kind");
            return false;
        }

        body = request.Body;
        reportKind = request.ReportKind;
        return true;
    }

    private static bool TryResolveReplyMetadata(
        InboxMessageType itemType,
        string? requestedReportKind,
        out ReportKind? reportKind,
        out DirectiveId? replyDirectiveId,
        out IResult? problem)
    {
        reportKind = null;
        replyDirectiveId = null;
        problem = null;

        if (itemType == InboxMessageType.Directive)
        {
            if (!ReportKindContract.TryParseWireValue(requestedReportKind, out var parsed))
            {
                problem = InvalidReply(
                    "report_kind must be 'progress' or 'done' when replying to a Directive.",
                    "report_kind");
                return false;
            }

            reportKind = parsed;
            return true;
        }

        if (requestedReportKind is not null)
        {
            problem = InvalidReply(
                "report_kind is only valid when replying to a Directive.",
                "report_kind");
            return false;
        }

        if (itemType == InboxMessageType.Escalation)
        {
            replyDirectiveId = DirectiveId.New();
        }

        return true;
    }

    private static InboxReplyResponse ToReplyResponse(OccupantReplyEmissionResult emission)
    {
        var message = emission.Message
            ?? throw new InvalidOperationException("Accepted occupant reply result has no message.");
        var from = message.From as PositionEndpointRef
            ?? throw new InvalidOperationException("Human reply source is not a position.");
        var to = message.To as PositionEndpointRef
            ?? throw new InvalidOperationException("Human reply destination is not a position.");
        if (!Enum.TryParse<InboxMessageType>(
                message.GetType().Name,
                ignoreCase: false,
                out var type)
            || !Enum.IsDefined(type))
        {
            throw new InvalidOperationException(
                $"Human reply type '{message.GetType().Name}' has no public inbox mapping.");
        }

        return new InboxReplyResponse(
            emission.SourceMessageId.Value,
            message.Id.Value,
            type,
            from.PositionId.Value,
            to.PositionId.Value,
            message.Thread.Value,
            message switch
            {
                Report report => report.AboutDirectiveId.Value,
                Directive directive => directive.DirectiveId.Value,
                _ => null,
            });
    }

    private static InboxDecisionResponse ToDecisionResponse(OccupantReplyEmissionResult emission)
    {
        var decision = emission.Message as ApprovalDecision
            ?? throw new InvalidOperationException(
                "Accepted inbox decision result has no ApprovalDecision message.");
        var from = decision.From as PositionEndpointRef
            ?? throw new InvalidOperationException("Human approval source is not a position.");
        var to = decision.To as PositionEndpointRef
            ?? throw new InvalidOperationException("Human approval destination is not a position.");
        return new InboxDecisionResponse(
            decision.RequestId.Value,
            decision.Id.Value,
            decision.Approved,
            decision.Reason,
            from.PositionId.Value,
            to.PositionId.Value,
            decision.Thread.Value);
    }

    private static Priority MapPriority(InboxPriority priority) =>
        Enum.TryParse<Priority>(priority.ToString(), ignoreCase: false, out var mapped)
        && Enum.IsDefined(mapped)
            ? mapped
            : throw new InvalidOperationException(
                $"Inbox priority '{priority}' has no organizational message mapping.");

    private static async ValueTask<PersonOrganizationScope?> ResolveScopeAsync(
        OrganizationId organizationId,
        IOrganizationPrincipalResolver principalResolver,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var principal = await principalResolver.ResolveAsync(
                httpContext.User,
                cancellationToken)
            .ConfigureAwait(false);
        return principal.PersonScopeFor(organizationId);
    }

    private static bool TryCreateQuery(
        InboxQueryParameters parameters,
        out InboxListQuery? query,
        out string? error)
    {
        query = null;
        if (!TryParseEnum(parameters.MessageType, "type", out InboxMessageType? messageType, out error) ||
            !TryParseEnum(parameters.ReadState, "read_state", out InboxReadState? readState, out error) ||
            !TryParseEnum(parameters.ResponseState, "response_state", out InboxResponseState? responseState, out error) ||
            !TryParseEnum(parameters.Priority, "priority", out InboxPriority? priority, out error) ||
            !TryParseUtc(parameters.DeadlineFromUtc, "deadline_from_utc", out var deadlineFrom, out error) ||
            !TryParseUtc(parameters.DeadlineToUtc, "deadline_to_utc", out var deadlineTo, out error) ||
            !TryParseBoolean(parameters.ApprovalPending, "approval_pending", out var approvalPending, out error) ||
            !TryParsePageSize(parameters.PageSize, out var pageSize, out error) ||
            !TryValidateCursor(parameters.Cursor, out var cursor, out error))
        {
            return false;
        }

        if (deadlineFrom > deadlineTo)
        {
            error = "deadline_from_utc cannot follow deadline_to_utc.";
            return false;
        }

        query = new InboxListQuery(
            messageType,
            readState,
            responseState,
            priority,
            deadlineFrom,
            deadlineTo,
            approvalPending,
            pageSize,
            cursor);
        error = null;
        return true;
    }

    private static bool TryParseEnum<T>(
        string? value,
        string queryName,
        out T? parsed,
        out string? error)
        where T : struct, Enum
    {
        parsed = null;
        if (value is null)
        {
            error = null;
            return true;
        }

        if (!Enum.TryParse<T>(value, ignoreCase: true, out var candidate) ||
            !Enum.IsDefined(candidate))
        {
            error = $"{queryName} is not a supported value.";
            return false;
        }

        parsed = candidate;
        error = null;
        return true;
    }

    private static bool TryParseUtc(
        string? value,
        string queryName,
        out DateTimeOffset? parsed,
        out string? error)
    {
        parsed = null;
        if (value is null)
        {
            error = null;
            return true;
        }

        var hasExplicitUtcDesignator =
            value.EndsWith('Z') ||
            value.EndsWith("+00:00", StringComparison.Ordinal) ||
            value.EndsWith("-00:00", StringComparison.Ordinal);
        if (!hasExplicitUtcDesignator ||
            !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var candidate) ||
            candidate.Offset != TimeSpan.Zero)
        {
            error = $"{queryName} must be an ISO 8601 timestamp with a UTC offset.";
            return false;
        }

        parsed = candidate;
        error = null;
        return true;
    }

    private static bool TryParseBoolean(
        string? value,
        string queryName,
        out bool? parsed,
        out string? error)
    {
        parsed = null;
        if (value is null)
        {
            error = null;
            return true;
        }

        if (!bool.TryParse(value, out var candidate))
        {
            error = $"{queryName} must be true or false.";
            return false;
        }

        parsed = candidate;
        error = null;
        return true;
    }

    private static bool TryParsePageSize(
        string? value,
        out int pageSize,
        out string? error)
    {
        pageSize = InboxListQuery.DefaultPageSize;
        if (value is null)
        {
            error = null;
            return true;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) ||
            pageSize is < 1 or > InboxListQuery.MaximumPageSize)
        {
            error = $"page_size must be between 1 and {InboxListQuery.MaximumPageSize}.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateCursor(
        string? value,
        out string? cursor,
        out string? error)
    {
        cursor = null;
        if (value is null)
        {
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > 2_048)
        {
            error = "cursor must contain between 1 and 2048 characters without surrounding whitespace.";
            return false;
        }

        cursor = value;
        error = null;
        return true;
    }

    private static bool TryParseOrganizationId(
        string value,
        out OrganizationId? organizationId) =>
        TryParse(value, OrganizationId.From, out organizationId);

    private static bool TryParsePositionId(string value, out PositionId? positionId) =>
        TryParse(value, PositionId.From, out positionId);

    private static bool TryParseItemId(string value, out string? itemId)
    {
        itemId = null;
        try
        {
            var decoded = Uri.UnescapeDataString(value);
            if (string.IsNullOrWhiteSpace(decoded) ||
                !string.Equals(decoded, decoded.Trim(), StringComparison.Ordinal) ||
                decoded.Length > 512)
            {
                return false;
            }

            itemId = decoded;
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool TryParse<T>(
        string value,
        Func<string, T> parser,
        out T? parsed)
        where T : class
    {
        try
        {
            parsed = parser(value);
            return true;
        }
        catch (ArgumentException)
        {
            parsed = null;
            return false;
        }
    }

    private static IResult InvalidOrganizationId() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid organization identifier");

    private static IResult InvalidPositionId() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid position identifier");

    private static IResult InvalidItemId() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid inbox item identifier");

    private static IResult InvalidQuery(string detail) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid inbox query",
            detail: detail);

    private static IResult InvalidReply(string detail, string path) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid inbox reply",
                Detail = detail,
                Extensions =
                {
                    ["path"] = path,
                },
            });

    private static IResult InvalidDecision(string detail, string path) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid inbox decision",
                Detail = detail,
                Extensions =
                {
                    ["path"] = path,
                },
            });

    private static IResult InvalidDraft(string detail, string path) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid inbox draft",
                Detail = detail,
                Extensions =
                {
                    ["path"] = path,
                },
            });

    private static IResult ReplyRejected(OccupantReplyEmissionResult emission) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Inbox reply rejected",
                Detail = "The occupied position rejected the organizational response.",
                Extensions =
                {
                    ["errors"] = emission.Errors
                        .Select(error => new InboxEmissionErrorResponse(
                            error.Code,
                            error.Path,
                            RejectionReasonContract.ToWireValue(error.Reason)))
                        .ToArray(),
                },
            });

    private static IResult DecisionRejected(OccupantReplyEmissionResult emission) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Inbox decision rejected",
                Detail = "The occupied position rejected the approval decision.",
                Extensions =
                {
                    ["errors"] = emission.Errors
                        .Select(error => new InboxEmissionErrorResponse(
                            error.Code,
                            error.Path,
                            RejectionReasonContract.ToWireValue(error.Reason)))
                        .ToArray(),
                },
            });

    private static IResult OrganizationNotFound() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Organization not found");

    private static IResult PositionNotFound() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Position not found");

    private static IResult InboxItemNotFound() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Inbox item not found");

    private static IResult ReadModelUnavailable() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Inbox read model unavailable");

    private static IResult ReplyEmissionUnavailable() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Inbox reply emission unavailable");

    private static IResult DecisionEmissionUnavailable() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Inbox decision emission unavailable");

    private static IResult InteractionStoreUnavailable() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Inbox interaction store unavailable");
}

internal sealed record InboxEmissionErrorResponse(
    string Code,
    string Path,
    string Reason);

internal sealed class InboxQueryParameters
{
    [FromQuery(Name = "type")]
    public string? MessageType { get; init; }

    [FromQuery(Name = "read_state")]
    public string? ReadState { get; init; }

    [FromQuery(Name = "response_state")]
    public string? ResponseState { get; init; }

    [FromQuery(Name = "priority")]
    public string? Priority { get; init; }

    [FromQuery(Name = "deadline_from_utc")]
    public string? DeadlineFromUtc { get; init; }

    [FromQuery(Name = "deadline_to_utc")]
    public string? DeadlineToUtc { get; init; }

    [FromQuery(Name = "approval_pending")]
    public string? ApprovalPending { get; init; }

    [FromQuery(Name = "page_size")]
    public string? PageSize { get; init; }

    [FromQuery(Name = "cursor")]
    public string? Cursor { get; init; }
}
