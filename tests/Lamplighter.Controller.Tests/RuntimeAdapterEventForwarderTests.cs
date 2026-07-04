using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tradecraft.Contracts.AgentRuntime.V1;
using Xunit;

namespace Lamplighter.Controller.Tests;

public sealed class RuntimeAdapterEventForwarderTests
{
    [Fact]
    public async Task ForwardOnceAsync_WhenAdapterEventsAreAvailable_ThenPublishesEventsAndAdvancesCursor()
    {
        // Arrange
        var workspace = Path.Combine(Path.GetTempPath(), $"adapter_events_{Guid.NewGuid():N}");
        var adapter = new FakeRuntimeAdapter();
        var api = new FakeRunnerApiClient();
        var forwarder = new RuntimeAdapterEventForwarder(
            adapter,
            api,
            Options.Create(new RunnerOptions
            {
                RunnerId = "controller_1",
                ControllerWorkspace = workspace,
                PollInterval = TimeSpan.FromSeconds(1)
            }),
            NullLogger<RuntimeAdapterEventForwarder>.Instance);

        // Act
        var firstForwarded = await forwarder.ForwardOnceAsync(CancellationToken.None);
        var secondForwarded = await forwarder.ForwardOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, firstForwarded);
        Assert.Equal(0, secondForwarded);
        Assert.Equal([1, 3], adapter.FromSequences);
        var cursor = await File.ReadAllTextAsync(
            Path.Combine(workspace, "controller-state", "adapter-events.cursor"),
            CancellationToken.None);
        Assert.Equal("3", cursor);
        Assert.Equal(2, api.PublishedEvents.Count);
        Assert.Equal(ControllerEventTypes.AgentRuntimeReady, api.PublishedEvents[0].EventType);
        Assert.Equal(ControllerEventTypes.DocumentPublicationRequested, api.PublishedEvents[1].EventType);
        Assert.Equal("event_1", api.PublishedEvents[0].Correlation.CausationId);
        Assert.Equal("ok", api.PublishedEvents[0].Payload?.GetProperty("status").GetString());
        var sourceEvent = api.PublishedEvents[0].Extensions?["tradecraft.dev/adapter_event"];
        Assert.Equal("adapter.event", sourceEvent?.GetProperty("message_type").GetString());
    }

    private sealed class FakeRuntimeAdapter : IAgentRuntimeAdapter
    {
        public List<long> FromSequences { get; } = [];

        public Task<AgentRuntimeAdapterResult> ReadEventsAsync(
            AgentRuntimeAdapterRequest request,
            CancellationToken cancellationToken)
        {
            var replay = JsonSerializer.Deserialize<AdapterEventReplayRequest>(
                request.Payload ?? throw new InvalidOperationException("Replay request payload was empty."),
                ControllerProtocolJson.Options) ?? throw new InvalidOperationException("Replay request was empty.");
            FromSequences.Add(replay.FromSequence);
            var batch = replay.FromSequence == 1
                ? CreateBatch(1, 2, [RuntimeReadyEvent(), DocumentPublicationEvent()])
                : CreateBatch(replay.FromSequence, replay.FromSequence - 1, []);
            var payload = JsonSerializer.Serialize(batch, ControllerProtocolJson.Options);
            return Task.FromResult(AgentRuntimeAdapterResult.Success(
                payload,
                "application/vnd.tradecraft.adapter-event-batch+json"));
        }

        public Task<AgentRuntimeAdapterResult> InspectRuntimeAsync(
            AgentRuntimeAdapterRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentRuntimeAdapterResult> PublishDocumentAsync(
            AgentRuntimeAdapterRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentRuntimeAdapterResult> StartRuntimeAsync(
            AgentRuntimeAdapterRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentRuntimeAdapterResult> StopRuntimeAsync(
            AgentRuntimeAdapterRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentRuntimeAdapterResult> CreateSessionAsync(
            AgentRuntimeAdapterRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentRuntimeAdapterResult> StartInvocationAsync(
            AgentRuntimeAdapterRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentRuntimeAdapterResult> CloseSessionAsync(
            AgentRuntimeAdapterRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentRuntimeAdapterResult> SynchronizeSessionHistoryAsync(
            AgentRuntimeAdapterRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        private static AdapterEventBatch CreateBatch(long fromSequence, long throughSequence, AdapterEvent[] events)
        {
            return new AdapterEventBatch(
                DocumentType: "adapter_event_batch",
                AdapterKind: "opencode",
                AdapterVersion: "0.1.0",
                FromSequence: fromSequence,
                ThroughSequence: throughSequence,
                NextSequence: throughSequence + 1,
                Events: [.. events],
                Exhausted: true,
                GeneratedAt: DateTimeOffset.Parse("2026-07-02T00:00:00Z", CultureInfo.InvariantCulture));
        }

        private static AdapterEvent RuntimeReadyEvent()
        {
            return CreateEvent("event_1", "runtime.ready", "runtime", "agent_1", 1);
        }

        private static AdapterEvent DocumentPublicationEvent()
        {
            return CreateEvent("event_2", "document.publication_requested", "invocation", "turn_1", 2);
        }

        private static AdapterEvent CreateEvent(
            string eventId,
            string eventType,
            string aggregateType,
            string aggregateId,
            long sequence)
        {
            using var payload = JsonDocument.Parse("""{"status":"ok"}""");
            return new AdapterEvent(
                MessageType: "adapter.event",
                ProtocolVersion: ControllerProtocolVersions.Protocol,
                SchemaVersion: ControllerProtocolVersions.Schema,
                EventId: eventId,
                AdapterKind: "opencode",
                AdapterVersion: "0.1.0",
                EventType: eventType,
                Aggregate: new AggregateReference(aggregateType, aggregateId),
                Sequence: sequence,
                OccurredAt: DateTimeOffset.Parse("2026-07-02T00:00:00Z", CultureInfo.InvariantCulture),
                PayloadSchemaVersion: ControllerProtocolVersions.Schema,
                Target: new ResourceTarget(
                    ControllerId: "controller_1",
                    RuntimeId: "agent_1",
                    AgentSessionId: "session_1",
                    InvocationId: "turn_1"),
                Correlation: new ProtocolCorrelation(
                    CommandId: "cmd_one",
                    InvocationId: "turn_1"),
                Payload: payload.RootElement.Clone());
        }
    }

    private sealed class FakeRunnerApiClient : IRunnerApiClient
    {
        public List<ControllerEvent> PublishedEvents { get; } = [];

        public Task UpsertHeartbeatAsync(
            ControllerHeartbeat heartbeat,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyCollection<ControllerCommand>> PollCommandsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<ControllerCommand>>([]);

        public Task<ControllerCommand> AcknowledgeCommandAsync(
            ControllerCommand command,
            CancellationToken cancellationToken) => Task.FromResult(command);

        public Task CompleteCommandAsync(
            ControllerCommand command,
            RunnerCommandResult result,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> DownloadContentStringAsync(
            ContentReference contentRef,
            CancellationToken cancellationToken) => Task.FromResult("");

        public Task<byte[]> DownloadContentBytesAsync(
            ContentReference contentRef,
            CancellationToken cancellationToken) => Task.FromResult(Array.Empty<byte>());

        public Task<ContentReference> UploadContentAsync(
            string content,
            string contentType,
            CancellationToken cancellationToken) => Task.FromResult(Content(contentType, content.Length));

        public Task<ContentReference> UploadContentAsync(
            byte[] bytes,
            string contentType,
            CancellationToken cancellationToken) => Task.FromResult(Content(contentType, bytes.Length));

        public Task PublishEventAsync(
            ControllerEvent controllerEvent,
            CancellationToken cancellationToken)
        {
            PublishedEvents.Add(controllerEvent);
            return Task.CompletedTask;
        }

        private static ContentReference Content(string contentType, long length)
        {
            return new ContentReference(
                "tradecraft://content/test",
                new string('0', 64),
                contentType,
                length);
        }
    }

}
