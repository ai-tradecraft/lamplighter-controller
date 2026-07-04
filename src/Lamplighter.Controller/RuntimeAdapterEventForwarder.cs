using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tradecraft.Contracts.AgentRuntime.V1;

namespace Lamplighter.Controller;

internal sealed partial class RuntimeAdapterEventForwarder(
    IAgentRuntimeAdapter runtimeAdapter,
    IRunnerApiClient apiClient,
    IOptions<RunnerOptions> options,
    ILogger<RuntimeAdapterEventForwarder> logger)
{
    private const long InitialSequence = 1;
    private const int MaxEventsPerBatch = 100;
    private readonly RunnerOptions _options = options.Value;

    public async Task<int> ForwardOnceAsync(CancellationToken cancellationToken)
    {
        var fromSequence = ReadCursor();
        var request = CreateReadEventsRequest(fromSequence);
        var output = await runtimeAdapter.ReadEventsAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!output.Succeeded)
        {
            LogRuntimeAdapterEventReplayFailed(fromSequence);
            return 0;
        }

        var batch = DeserializeBatch(output.Payload);
        foreach (var adapterEvent in batch.Events)
        {
            await apiClient.PublishEventAsync(ToControllerEvent(adapterEvent), cancellationToken)
                .ConfigureAwait(false);
        }

        if (batch.NextSequence > fromSequence)
        {
            WriteCursor(batch.NextSequence);
        }

        return batch.Events.Length;
    }

    private AgentRuntimeAdapterRequest CreateReadEventsRequest(long fromSequence)
    {
        var payload = JsonSerializer.Serialize(
            new AdapterEventReplayRequest(
                DocumentType: "adapter_event_replay_request",
                FromSequence: fromSequence,
                MaxEvents: MaxEventsPerBatch),
            ControllerProtocolJson.Options);
        var commandId = $"cmd_adapter_events_{fromSequence}";
        return new AgentRuntimeAdapterRequest(
            CommandId: commandId,
            Target: new ResourceTarget(ControllerId: _options.RunnerId),
            IdempotencyKey: $"adapter-events:{fromSequence}",
            Deadline: DateTimeOffset.UtcNow.Add(_options.PollInterval),
            FencingToken: 1,
            Correlation: new ProtocolCorrelation(CommandId: commandId),
            AuthorizationContext: new AuthorizationContext(
                SubjectRef: $"system://controller/{_options.RunnerId}",
                GrantRef: $"authorization-grant://controller/{_options.RunnerId}/adapter-events",
                IssuedAt: DateTimeOffset.UtcNow),
            Payload: payload,
            PayloadContentType: "application/vnd.tradecraft.adapter-event-replay-request+json");
    }

    private ControllerEvent ToControllerEvent(AdapterEvent adapterEvent)
    {
        return new ControllerEvent(
            MessageType: ControllerMessageTypes.Event,
            ProtocolVersion: ControllerProtocolVersions.Protocol,
            SchemaVersion: ControllerProtocolVersions.Schema,
            EventId: $"controller_{adapterEvent.EventId}",
            ControllerId: _options.RunnerId,
            EventType: MapEventType(adapterEvent.EventType),
            Aggregate: adapterEvent.Aggregate,
            Sequence: adapterEvent.Sequence,
            OccurredAt: adapterEvent.OccurredAt,
            PayloadSchemaVersion: adapterEvent.PayloadSchemaVersion,
            Target: adapterEvent.Target,
            Correlation: adapterEvent.Correlation with
            {
                CausationId = adapterEvent.EventId
            },
            PayloadRef: adapterEvent.PayloadRef,
            Payload: adapterEvent.Payload,
            RawEventRef: adapterEvent.RawEventRef,
            Extensions: ImmutableDictionary<string, JsonElement>.Empty.Add(
                "tradecraft.dev/adapter_event",
                JsonSerializer.SerializeToElement(adapterEvent, ControllerProtocolJson.Options)));
    }

    private static string MapEventType(string adapterEventType)
    {
        return adapterEventType switch
        {
            "runtime.started" or "runtime.ready" => ControllerEventTypes.AgentRuntimeReady,
            "runtime.stopped" => ControllerEventTypes.AgentRuntimeStopped,
            "runtime.degraded" or "runtime.lost" => ControllerEventTypes.AgentRuntimeLost,
            "session.created" => ControllerEventTypes.AgentSessionCreated,
            "session.closed" => ControllerEventTypes.AgentSessionClosed,
            "invocation.progress" or "invocation.output_delta" => ControllerEventTypes.ProgressReported,
            "invocation.outcome_reported" or "invocation.stopped" => ControllerEventTypes.OutcomeReported,
            "invocation.interaction_requested" => ControllerEventTypes.InteractionRequested,
            "interaction.opened" => ControllerEventTypes.InteractionSessionOpened,
            "interaction.message" => ControllerEventTypes.InteractionMessageReceived,
            "interaction.acknowledged" => ControllerEventTypes.InteractionMessageAcknowledged,
            "interaction.closed" or "interaction.disconnected" => ControllerEventTypes.InteractionSessionClosed,
            "snapshot.suggested" => ControllerEventTypes.SnapshotSuggested,
            "snapshot.capture_started" => ControllerEventTypes.SnapshotCaptureStarted,
            "snapshot.content_ready" => ControllerEventTypes.SnapshotContentReady,
            "snapshot.capture_failed" => ControllerEventTypes.SnapshotCaptureFailed,
            "document.publication_requested" => ControllerEventTypes.DocumentPublicationRequested,
            _ => adapterEventType,
        };
    }

    private static AdapterEventBatch DeserializeBatch(string payload)
    {
        return JsonSerializer.Deserialize<AdapterEventBatch>(payload, ControllerProtocolJson.Options)
               ?? throw new InvalidOperationException("Runtime adapter returned an empty adapter event batch.");
    }

    private long ReadCursor()
    {
        var path = CursorPath();
        if (!File.Exists(path))
        {
            return InitialSequence;
        }

        var value = File.ReadAllText(path);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
               && sequence >= InitialSequence
            ? sequence
            : throw new InvalidOperationException($"Invalid runtime adapter event cursor value: {value}");
    }

    private void WriteCursor(long nextSequence)
    {
        var path = CursorPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, nextSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private string CursorPath()
    {
        return Path.Combine(
            _options.ControllerWorkspace,
            "controller-state",
            "adapter-events.cursor");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime adapter event replay failed; event cursor remains at {FromSequence}.")]
    private partial void LogRuntimeAdapterEventReplayFailed(long fromSequence);
}

internal sealed partial class RuntimeAdapterEventLoop(
    RuntimeAdapterEventForwarder forwarder,
    IOptions<RunnerOptions> options,
    ILogger<RuntimeAdapterEventLoop> logger)
{
    private readonly RunnerOptions _options = options.Value;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        LogRuntimeAdapterEventForwardingLoopStarted();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var forwarded = await forwarder.ForwardOnceAsync(stoppingToken)
                    .ConfigureAwait(false);
                if (forwarded > 0)
                {
                    LogRuntimeAdapterEventsForwarded(forwarded);
                }
                await Task.Delay(_options.PollInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException or UnauthorizedAccessException)
            {
                LogRuntimeAdapterEventForwardingFailed(ex, _options.RetryBackoff);
                await Task.Delay(_options.RetryBackoff, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        LogRuntimeAdapterEventForwardingLoopStopped();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Runtime adapter event forwarding loop started.")]
    private partial void LogRuntimeAdapterEventForwardingLoopStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Forwarded {EventCount} runtime adapter event(s).")]
    private partial void LogRuntimeAdapterEventsForwarded(int eventCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime adapter event forwarding failed; backing off for {Backoff}.")]
    private partial void LogRuntimeAdapterEventForwardingFailed(Exception exception, TimeSpan backoff);

    [LoggerMessage(Level = LogLevel.Information, Message = "Runtime adapter event forwarding loop stopped.")]
    private partial void LogRuntimeAdapterEventForwardingLoopStopped();
}
