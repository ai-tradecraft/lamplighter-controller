using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tradecraft.Contracts.AgentRuntime.V1;

namespace Lamplighter.Controller;

internal interface IRunnerCommandHandler
{
    Task<RunnerCommandResult> HandleAsync(
        ControllerCommand command,
        CancellationToken cancellationToken);
}

internal sealed record RunnerCommandResult(
    string DeliveryStatus,
    ContentReference? ResultRef,
    ProtocolError? Error)
{
    public static RunnerCommandResult Completed(ContentReference? resultRef = null)
    {
        return new RunnerCommandResult(CommandDeliveryStatuses.Completed, resultRef, null);
    }

    public static RunnerCommandResult Failed(ProtocolError error)
    {
        return new RunnerCommandResult(CommandDeliveryStatuses.Failed, null, error);
    }
}

internal sealed class CliRunnerCommandHandler(
    IRunnerApiClient apiClient,
    IAgentRuntimeAdapter runtimeAdapter,
    IOptions<RunnerOptions> options,
    ILogger<CliRunnerCommandHandler> logger) : IRunnerCommandHandler
{
    private readonly RunnerOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, long> _aggregateSequences = new();

    public async Task<RunnerCommandResult> HandleAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        return command.CommandType switch
        {
            ControllerCommandTypes.StartAgentRuntime =>
                await PrepareAgentAsync(command, cancellationToken),
            ControllerCommandTypes.StopAgentRuntime =>
                await StopAgentAsync(command, cancellationToken),
            ControllerCommandTypes.CreateAgentSession =>
                await CreateSessionAsync(command, cancellationToken),
            ControllerCommandTypes.StartInvocation =>
                await SubmitTurnAsync(command, cancellationToken),
            ControllerCommandTypes.SynchronizeSessionHistory =>
                await SyncSessionHistoryAsync(command, cancellationToken),
            ControllerCommandTypes.CloseAgentSession =>
                await CancelSessionAsync(command, cancellationToken),
            _ => RunnerCommandResult.Failed(new ProtocolError(
                Code: "unsupported_controller_command",
                Classification: ProtocolErrorClassifications.UnsupportedOperation,
                Summary: $"Unsupported command type {command.CommandType}.",
                Retryable: false))
        };
    }

    private async Task<RunnerCommandResult> PrepareAgentAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var output = await runtimeAdapter.StartRuntimeAsync(
            new AgentRuntimeAdapterRequest(
                CommandId: command.CommandId,
                RuntimeId: RequiredAgentId(command),
                Payload: await DownloadPayloadAsync(command, cancellationToken)),
            cancellationToken);
        return await CompleteFromAdapterAsync(
            command,
            output,
            ControllerEventTypes.AgentRuntimeReady,
            ControllerEventTypes.AgentRuntimeLost,
            cancellationToken);
    }

    private async Task<RunnerCommandResult> CreateSessionAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var output = await runtimeAdapter.CreateSessionAsync(
            new AgentRuntimeAdapterRequest(
                CommandId: command.CommandId,
                RuntimeId: RequiredAgentId(command),
                SessionId: RequiredAgentSessionId(command),
                Payload: await DownloadPayloadAsync(command, cancellationToken)),
            cancellationToken);
        return await CompleteFromAdapterAsync(
            command,
            output,
            ControllerEventTypes.AgentSessionCreated,
            ControllerEventTypes.AgentSessionClosed,
            cancellationToken);
    }

    private async Task<RunnerCommandResult> StopAgentAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var output = await runtimeAdapter.StopRuntimeAsync(
            new AgentRuntimeAdapterRequest(
                CommandId: command.CommandId,
                RuntimeId: RequiredAgentId(command)),
            cancellationToken);
        return await CompleteFromAdapterAsync(
            command,
            output,
            ControllerEventTypes.AgentRuntimeStopped,
            ControllerEventTypes.AgentRuntimeLost,
            cancellationToken);
    }

    private async Task<RunnerCommandResult> SubmitTurnAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var payload = await DownloadPayloadAsync(command, cancellationToken);
        var sessionId = RequiredAgentSessionId(command);
        var output = await runtimeAdapter.StartInvocationAsync(
            new AgentRuntimeAdapterRequest(
                CommandId: command.CommandId,
                RuntimeId: command.Target.RuntimeId,
                SessionId: sessionId,
                InvocationId: InvocationId(command),
                Payload: payload),
            cancellationToken);

        return await CompleteTurnFromAdapterAsync(command, output, cancellationToken);
    }

    private async Task<RunnerCommandResult> CancelSessionAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var sessionId = RequiredAgentSessionId(command);
        var runtimeId = command.Target.RuntimeId;
        logger.LogInformation(
            "Received close command {CommandId} for session {SessionId}.",
            command.CommandId,
            sessionId);
        if (runtimeId is not null)
        {
            var output = await runtimeAdapter.CloseSessionAsync(
                new AgentRuntimeAdapterRequest(
                    CommandId: command.CommandId,
                    RuntimeId: runtimeId,
                    SessionId: sessionId),
                cancellationToken);
            return await CompleteFromAdapterAsync(
                command,
                output,
                ControllerEventTypes.AgentSessionClosed,
                ControllerEventTypes.AgentSessionClosed,
                cancellationToken);
        }
        var payloadRef = command.PayloadRef is null
            ? null
            : await apiClient.UploadContentAsync(
                await apiClient.DownloadContentStringAsync(command.PayloadRef, cancellationToken),
                command.PayloadRef.ContentType,
                cancellationToken);
        await PublishEventAsync(
            command,
            ControllerEventTypes.AgentSessionClosed,
            payloadRef,
            cancellationToken);
        return RunnerCommandResult.Completed(payloadRef);
    }

    private async Task<RunnerCommandResult> SyncSessionHistoryAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var sessionId = RequiredAgentSessionId(command);
        var output = await runtimeAdapter.SynchronizeSessionHistoryAsync(
            new AgentRuntimeAdapterRequest(
                CommandId: command.CommandId,
                RuntimeId: RequiredAgentId(command),
                SessionId: sessionId),
            cancellationToken);
        return await CompleteFromAdapterAsync(
            command,
            output,
            ControllerEventTypes.AgentSessionHistorySynchronized,
            ControllerEventTypes.AgentSessionHistorySynchronizationFailed,
            cancellationToken);
    }

    private async Task<RunnerCommandResult> CompleteFromAdapterAsync(
        ControllerCommand command,
        AgentRuntimeAdapterResult output,
        string successEventType,
        string failureEventType,
        CancellationToken cancellationToken)
    {
        if (output.Succeeded)
        {
            var resultRef = await apiClient.UploadContentAsync(output.Payload, output.ContentType, cancellationToken);
            await PublishEventAsync(command, successEventType, resultRef, cancellationToken);
            return RunnerCommandResult.Completed(resultRef);
        }

        return await CompleteAdapterFailureAsync(command, output, failureEventType, cancellationToken);
    }

    private async Task<RunnerCommandResult> CompleteTurnFromAdapterAsync(
        ControllerCommand command,
        AgentRuntimeAdapterResult output,
        CancellationToken cancellationToken)
    {
        if (!output.Succeeded)
        {
            return await CompleteAdapterFailureAsync(
                command,
                output,
                ControllerEventTypes.OutcomeReported,
                cancellationToken);
        }

        string? resultStatus;
        try
        {
            using var result = JsonDocument.Parse(output.Payload);
            resultStatus = result.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                    ? status.GetString()
                    : null;
        }
        catch (JsonException ex)
        {
            var invalidOutput = output with
            {
                Succeeded = false,
                Payload = $"Runtime adapter returned invalid AgentTurnResult JSON: {ex.Message}",
                ContentType = "text/plain"
            };
            return await CompleteAdapterFailureAsync(
                command,
                invalidOutput,
                ControllerEventTypes.OutcomeReported,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(resultStatus))
        {
            var invalidOutput = output with
            {
                Succeeded = false,
                Payload = "Runtime adapter AgentTurnResult JSON did not contain a status.",
                ContentType = "text/plain"
            };
            return await CompleteAdapterFailureAsync(
                command,
                invalidOutput,
                ControllerEventTypes.OutcomeReported,
                cancellationToken);
        }

        var resultRef = await apiClient.UploadContentAsync(
            output.Payload,
            output.ContentType,
            cancellationToken);
        await PublishEventAsync(
            command,
            ControllerEventTypes.OutcomeReported,
            resultRef,
            cancellationToken);
        return RunnerCommandResult.Completed(resultRef);
    }

    private async Task<RunnerCommandResult> CompleteAdapterFailureAsync(
        ControllerCommand command,
        AgentRuntimeAdapterResult output,
        string failureEventType,
        CancellationToken cancellationToken)
    {
        var failureRef = await apiClient.UploadContentAsync(
            output.Payload,
            output.ContentType,
            cancellationToken);
        var failure = new ProtocolError(
            Code: "runtime_adapter_command_failed",
            Classification: ProtocolErrorClassifications.AdapterFailure,
            Summary: output.ExitCode is null
                ? "Runtime adapter operation failed."
                : $"Runtime adapter operation failed with exit code {output.ExitCode}.",
            Retryable: false,
            DiagnosticRef: failureRef);
        await PublishEventAsync(command, failureEventType, failureRef, cancellationToken);
        return RunnerCommandResult.Failed(failure);
    }

    private async Task<string> DownloadPayloadAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        if (command.PayloadRef is null)
        {
            throw new InvalidOperationException(
                $"Command {command.CommandId} does not have a payload_ref.");
        }

        return await apiClient.DownloadContentStringAsync(command.PayloadRef, cancellationToken);
    }

    private async Task PublishEventAsync(
        ControllerCommand command,
        string eventType,
        ContentReference? payloadRef,
        CancellationToken cancellationToken)
    {
        var aggregate = Aggregate(command);
        var sequence = _aggregateSequences.AddOrUpdate(
            $"{aggregate.Type}:{aggregate.Id}",
            1,
            static (_, current) => current + 1);
        await apiClient.PublishEventAsync(
            new ControllerEvent(
                MessageType: ControllerMessageTypes.Event,
                ProtocolVersion: ControllerProtocolVersions.Protocol,
                SchemaVersion: ControllerProtocolVersions.Schema,
                EventId: $"event_{Guid.NewGuid():N}",
                ControllerId: _options.RunnerId,
                EventType: eventType,
                Aggregate: aggregate,
                Sequence: sequence,
                OccurredAt: DateTimeOffset.UtcNow,
                PayloadSchemaVersion: ControllerProtocolVersions.Schema,
                Target: command.Target,
                Correlation: command.Correlation with
                {
                    CommandId = command.CommandId,
                    CausationId = command.CommandId
                },
                FencingToken: command.Execution.Lease?.FencingToken,
                PayloadRef: payloadRef,
                Payload: payloadRef is null
                    ? JsonSerializer.SerializeToElement(new { })
                    : null),
            cancellationToken);
    }

    private static string RequiredAgentId(ControllerCommand command)
    {
        return SafeIdentifier(
            command.Target.RuntimeId
            ?? throw new InvalidOperationException(
                $"Command {command.CommandId} does not target a runtime."),
            "agent_");
    }

    private static string RequiredAgentSessionId(ControllerCommand command)
    {
        return command.Target.AgentSessionId
               ?? throw new InvalidOperationException(
                   $"Command {command.CommandId} does not target an agent session.");
    }

    private static string InvocationId(ControllerCommand command)
    {
        return command.Target.InvocationId
               ?? command.Correlation.InvocationId
               ?? command.Correlation.CorrelationId
               ?? command.CommandId;
    }

    private static AggregateReference Aggregate(ControllerCommand command)
    {
        if (command.Target.InvocationId is { } invocationId)
        {
            return new AggregateReference("invocation", invocationId);
        }

        if (command.Target.AgentSessionId is { } sessionId)
        {
            return new AggregateReference("agent_session", sessionId);
        }

        if (command.Target.RuntimeId is { } runtimeId)
        {
            return new AggregateReference("runtime", runtimeId);
        }

        return new AggregateReference("controller", command.Target.ControllerId ?? "controller_unknown");
    }

    private static string SafeIdentifier(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new InvalidOperationException($"Unsafe identifier: {value}");
        }
        return value;
    }
}
