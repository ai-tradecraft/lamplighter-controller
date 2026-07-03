using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tradecraft.Contracts.AgentRuntime.V1;
using Xunit;

namespace Lamplighter.Controller.Tests;

public sealed class CliRunnerCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenStartingRuntime_ThenPublishesCanonicalReadyEvent()
    {
        // Arrange
        var contentRef = Content("agent_1", "application/json", 2);
        var api = new FakeRunnerApiClient("""{"agent_id":"agent_1"}""");
        var process = new FakeAdapterProcessRunner(
            new ProcessOutput(
                "uv run lamplighter-opencode adapter-operation",
                0,
                AdapterResult("""{"status":"ready"}""", "application/vnd.tradecraft.start-agent-result+json"),
                ""));
        var controllerWorkspace = NewRuntimeRoot();
        var handler = CreateHandler(api, process, controllerWorkspace);
        var command = Command(
            ControllerCommandTypes.StartAgentRuntime,
            contentRef,
            runtimeId: "agent_1");

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(CommandDeliveryStatuses.Completed, result.DeliveryStatus);
        var operation = ReadOperation(process);
        Assert.Equal("adapter.operation", operation.RootElement.GetProperty("message_type").GetString());
        Assert.Equal("StartRuntime", operation.RootElement.GetProperty("operation_type").GetString());
        Assert.Equal("agent_1", operation.RootElement.GetProperty("target").GetProperty("runtime_id").GetString());
        Assert.Equal("agent_1", operation.RootElement.GetProperty("payload").GetProperty("agent_id").GetString());
        Assert.Equal(
            controllerWorkspace,
            operation.RootElement.GetProperty("extensions").GetProperty("tradecraft.dev/controller_workspace").GetString());
        Assert.False(operation.RootElement.GetProperty("extensions").TryGetProperty("tradecraft.dev/payload", out _));
        Assert.Contains("adapter-operation", process.Arguments);
        Assert.Contains("--operation", process.Arguments);
        Assert.DoesNotContain("prepare-agent", process.Arguments);
        Assert.DoesNotContain("start-agent", process.Arguments);
        var published = Assert.Single(api.PublishedEvents);
        Assert.Equal(ControllerEventTypes.AgentRuntimeReady, published.EventType);
        Assert.Equal("agent_1", published.Target.RuntimeId);
        Assert.Equal("runtime", published.Aggregate.Type);
    }

    [Fact]
    public async Task HandleAsync_WhenCreatingAgentSession_ThenPublishesCanonicalSessionEvent()
    {
        // Arrange
        var contentRef = Content("spec_1", "application/json", 2);
        var api = new FakeRunnerApiClient(
            """{"agent_session_id":"session_1","workspace_ref":"/tmp/workspace"}""");
        var process = new FakeAdapterProcessRunner(
            new ProcessOutput(
                "uv run lamplighter-opencode adapter-operation",
                0,
                AdapterResult("""{"status":"ready"}""", "application/vnd.tradecraft.create-session-result+json"),
                ""));
        var handler = CreateHandler(api, process, NewRuntimeRoot());
        var command = Command(
            ControllerCommandTypes.CreateAgentSession,
            contentRef,
            runtimeId: "agent_1",
            sessionId: "session_1");

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(CommandDeliveryStatuses.Completed, result.DeliveryStatus);
        Assert.Equal("CreateSession", ReadOperation(process).RootElement.GetProperty("operation_type").GetString());
        Assert.Contains("adapter-operation", process.Arguments);
        Assert.DoesNotContain("create-session", process.Arguments);
        var published = Assert.Single(api.PublishedEvents);
        Assert.Equal(ControllerEventTypes.AgentSessionCreated, published.EventType);
        Assert.Equal("session_1", published.Target.AgentSessionId);
        Assert.Equal("agent_session", published.Aggregate.Type);
    }

    [Fact]
    public async Task HandleAsync_WhenInvocationAdapterFails_ThenReturnsClassifiedFailure()
    {
        // Arrange
        var api = new FakeRunnerApiClient(
            """{"id":"turn_1","agent_session_id":"session_1","instruction":"hello"}""");
        var process = new FakeAdapterProcessRunner(
            new ProcessOutput(
                "uv run lamplighter-opencode adapter-operation",
                0,
                FailedAdapterResult("OpenCode server request failed."),
                ""));
        var handler = CreateHandler(api, process, NewRuntimeRoot());
        var command = Command(
            ControllerCommandTypes.StartInvocation,
            Content("turn_1", "application/json", 2),
            runtimeId: "agent_1",
            sessionId: "session_1",
            invocationId: "turn_1");

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(CommandDeliveryStatuses.Failed, result.DeliveryStatus);
        Assert.Equal(
            ProtocolErrorClassifications.AdapterFailure,
            Assert.IsType<ProtocolError>(result.Error).Classification);
        Assert.Equal("StartInvocation", ReadOperation(process).RootElement.GetProperty("operation_type").GetString());
        Assert.Contains("adapter-operation", process.Arguments);
        Assert.DoesNotContain("submit-turn", process.Arguments);
        var published = Assert.Single(api.PublishedEvents);
        Assert.Equal(ControllerEventTypes.OutcomeReported, published.EventType);
        Assert.Equal("invocation", published.Aggregate.Type);
        Assert.Equal("text/plain", api.Uploads.Single().ContentType);
    }

    [Fact]
    public async Task HandleAsync_WhenSynchronizingHistory_ThenPublishesCanonicalHistoryEvent()
    {
        // Arrange
        var history = """
            {
              "agent_id": "agent_1",
              "agent_session_id": "session_1",
              "opencode_session_id": "oc_session_1",
              "observed_at": "2026-06-30T00:00:00Z",
              "messages": [],
              "raw_messages": []
            }
            """;
        var api = new FakeRunnerApiClient("");
        var process = new FakeAdapterProcessRunner(
            new ProcessOutput(
                "uv run lamplighter-opencode adapter-operation",
                0,
                AdapterResult(history, "application/vnd.tradecraft.agent-chat-history+json"),
                ""));
        var controllerWorkspace = NewRuntimeRoot();
        var handler = CreateHandler(api, process, controllerWorkspace);
        var command = Command(
            ControllerCommandTypes.SynchronizeSessionHistory,
            payloadRef: null,
            runtimeId: "agent_1",
            sessionId: "session_1");

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(CommandDeliveryStatuses.Completed, result.DeliveryStatus);
        var operation = ReadOperation(process);
        Assert.Equal("ReadTranscript", operation.RootElement.GetProperty("operation_type").GetString());
        Assert.Equal("agent_1", operation.RootElement.GetProperty("target").GetProperty("runtime_id").GetString());
        Assert.Equal("session_1", operation.RootElement.GetProperty("target").GetProperty("agent_session_id").GetString());
        Assert.False(operation.RootElement.TryGetProperty("payload", out _));
        Assert.Equal(
            controllerWorkspace,
            operation.RootElement.GetProperty("extensions").GetProperty("tradecraft.dev/controller_workspace").GetString());
        Assert.Equal(
            "application/vnd.tradecraft.agent-chat-history+json",
            api.Uploads.Single().ContentType);
        Assert.Equal(
            ControllerEventTypes.AgentSessionHistorySynchronized,
            api.PublishedEvents.Single().EventType);
    }

    [Fact]
    public async Task HandleAsync_WhenInvocationReturnsStructuredFailure_ThenDeliveryStillCompletes()
    {
        // Arrange
        var api = new FakeRunnerApiClient(
            """{"id":"turn_1","agent_session_id":"session_1","instruction":"hello"}""");
        var process = new FakeAdapterProcessRunner(new ProcessOutput(
            "uv run lamplighter-opencode adapter-operation",
            0,
            AdapterResult(
            """
            {
              "status": "failed",
              "message": "OpenCode server request failed.",
              "failure_report": {
                "summary": "OpenCode server request failed.",
                "detail": "Connection refused"
              }
            }
            """,
                "application/vnd.tradecraft.agent-turn-result+json"),
            ""));
        var handler = CreateHandler(api, process, NewRuntimeRoot());
        var command = Command(
            ControllerCommandTypes.StartInvocation,
            Content("turn_1", "application/json", 2),
            runtimeId: "agent_1",
            sessionId: "session_1",
            invocationId: "turn_1");

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(CommandDeliveryStatuses.Completed, result.DeliveryStatus);
        Assert.Null(result.Error);
        Assert.Equal(
            ControllerEventTypes.OutcomeReported,
            api.PublishedEvents.Single().EventType);
        Assert.Equal(
            "application/vnd.tradecraft.agent-turn-result+json",
            api.Uploads.Single().ContentType);
    }

    [Fact]
    public void ConfigureAdapterProcessEnvironment_RemovesProviderOwnedSettings()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND"] = "1",
            ["LAMPLIGHTER_OPENCODE_CONFIG_MODE"] = "project-only",
            ["LAMPLIGHTER_OPENCODE_MODEL"] = "azure/deployment",
            ["OPENCODE_MODEL"] = "anthropic/model",
            ["OPENCODE_CONFIG"] = "/tmp/opencode.json",
            ["OPENCODE_CONFIG_DIR"] = "/tmp/opencode",
            ["OPENCODE_CONFIG_CONTENT"] = "{}",
            ["AZURE_OPENAI_API_KEY"] = "secret",
            ["AZURE_OPENAI_ENDPOINT"] = "https://example.openai.azure.com/",
            ["AZURE_OPENAI_DEPLOYMENT"] = "deployment",
            ["AZURE_OPENAI_RESOURCE_NAME"] = "resource",
            ["ANTHROPIC_API_KEY"] = "secret",
            ["OPENAI_API_KEY"] = "secret",
            ["OPENCODE_DISABLE_AUTOUPDATE"] = "1"
        };

        CliAdapterProcessRunner.ConfigureAdapterProcessEnvironment(environment);

        Assert.Equal("1", environment["LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND"]);
        Assert.Equal("1", environment["OPENCODE_DISABLE_AUTOUPDATE"]);
        Assert.DoesNotContain(environment.Keys, key => key.Contains("MODEL", StringComparison.Ordinal));
        Assert.DoesNotContain(environment.Keys, key => key.Contains("API_KEY", StringComparison.Ordinal));
        Assert.False(environment.ContainsKey("LAMPLIGHTER_OPENCODE_CONFIG_MODE"));
        Assert.False(environment.ContainsKey("OPENCODE_CONFIG"));
        Assert.False(environment.ContainsKey("OPENCODE_CONFIG_DIR"));
        Assert.False(environment.ContainsKey("OPENCODE_CONFIG_CONTENT"));
    }

    private static CliRunnerCommandHandler CreateHandler(
        IRunnerApiClient api,
        IAdapterProcessRunner process,
        string controllerWorkspace)
    {
        var options = Options.Create(new RunnerOptions
        {
            RunnerId = "controller_1",
            ControllerWorkspace = controllerWorkspace,
            Adapter = OpenCodeCliAdapterOptions()
        });
        var transport = new CliAdapterTransport(process, options);
        var adapter = new CliAgentRuntimeAdapter(transport, options);
        return new CliRunnerCommandHandler(
            api,
            adapter,
            options,
            NullLogger<CliRunnerCommandHandler>.Instance);
    }

    private static ControllerCommand Command(
        string commandType,
        ContentReference? payloadRef,
        string? runtimeId = null,
        string? sessionId = null,
        string? invocationId = null)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new ControllerCommand(
            MessageType: ControllerMessageTypes.Command,
            ProtocolVersion: ControllerProtocolVersions.Protocol,
            SchemaVersion: ControllerProtocolVersions.Schema,
            CommandId: "cmd_1",
            CommandType: commandType,
            IdempotencyKey: "idempotency_1",
            IssuedAt: now,
            Target: new ResourceTarget(
                ControllerId: "controller_1",
                RuntimeId: runtimeId,
                AgentSessionId: sessionId,
                InvocationId: invocationId),
            Correlation: new ProtocolCorrelation(
                CommandId: "cmd_1",
                CorrelationId: invocationId ?? sessionId ?? runtimeId,
                InvocationId: invocationId),
            AuthorizationContext: new AuthorizationContext(
                "system://tests",
                "authorization-grant://tests/1",
                now),
            Execution: new CommandExecution(
                now.AddHours(1),
                "PT30S",
                new CommandLease(
                    "lease_1",
                    "controller_1",
                    now,
                    now.AddMinutes(1),
                    1,
                    1)),
            PayloadRef: payloadRef);
    }

    private static ContentReference Content(
        string id,
        string contentType,
        long length)
    {
        return new ContentReference(
            $"tradecraft://content/{id}",
            "sha",
            contentType,
            length);
    }

    private static JsonDocument ReadOperation(FakeAdapterProcessRunner process)
    {
        var operationIndex = Array.IndexOf(process.Arguments, "--operation");
        Assert.True(operationIndex >= 0);
        var operationPath = process.Arguments[operationIndex + 1];
        return JsonDocument.Parse(File.ReadAllText(operationPath));
    }

    private static string AdapterResult(
        string payload,
        string contentType)
    {
        var payloadPath = Path.Combine(Path.GetTempPath(), $"adapter_result_{Guid.NewGuid():N}.json");
        File.WriteAllText(payloadPath, payload);
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payloadBytes));
        return $$"""
            {
              "message_type": "adapter.operation_result",
              "protocol_version": "1.0",
              "schema_version": "1.0",
              "result_id": "result_cmd_1",
              "operation_id": "cmd_1",
              "status": "completed",
              "completed_at": "1970-01-01T00:00:00Z",
              "fencing_token": 1,
              "correlation": {
                "command_id": "cmd_1"
              },
              "result_ref": {
                "uri": "{{new Uri(payloadPath).AbsoluteUri}}",
                "sha256": "{{sha256}}",
                "content_type": "{{contentType}}",
                "length": {{payloadBytes.Length}}
              }
            }
            """;
    }

    private static string FailedAdapterResult(string summary)
    {
        return $$"""
            {
              "message_type": "adapter.operation_result",
              "protocol_version": "1.0",
              "schema_version": "1.0",
              "result_id": "result_cmd_1",
              "operation_id": "cmd_1",
              "status": "failed",
              "completed_at": "1970-01-01T00:00:00Z",
              "fencing_token": 1,
              "correlation": {
                "command_id": "cmd_1"
              },
              "error": {
                "code": "opencode_failed",
                "classification": "internal_adapter_error",
                "summary": "{{summary}}",
                "retryable": false
              }
            }
            """;
    }

    private static string NewRuntimeRoot()
    {
        return Path.Combine(Path.GetTempPath(), $"runner_handler_{Guid.NewGuid():N}");
    }

    private static RuntimeAdapterOptions OpenCodeCliAdapterOptions()
    {
        return new RuntimeAdapterOptions
        {
            Kind = "opencode-cli",
            Executable = "uv",
            ArgumentPrefix = ["run", "lamplighter-opencode"],
            Commands = new RuntimeAdapterCommandOptions
            {
                Operation = "adapter-operation"
            }
        };
    }

    private sealed class FakeAdapterProcessRunner(params ProcessOutput[] outputs)
        : IAdapterProcessRunner
    {
        private readonly Queue<ProcessOutput> _outputs = new(outputs);

        public string[] Arguments { get; private set; } = [];

        public List<ProcessInvocation> Invocations { get; } = [];

        public Task<ProcessOutput> RunAsync(
            string fileName,
            string[] arguments,
            CancellationToken cancellationToken)
        {
            Arguments = arguments;
            Invocations.Add(new ProcessInvocation(fileName, arguments));
            return Task.FromResult(_outputs.Dequeue());
        }
    }

    private sealed record ProcessInvocation(string FileName, string[] Arguments);

    private sealed class FakeRunnerApiClient(string payload) : IRunnerApiClient
    {
        public List<ControllerEvent> PublishedEvents { get; } = [];

        public List<ContentReference> Uploads { get; } = [];

        public Task UpsertHeartbeatAsync(
            ControllerHeartbeat heartbeat,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyCollection<ControllerCommand>> PollCommandsAsync(
            CancellationToken cancellationToken) =>
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
            CancellationToken cancellationToken) => Task.FromResult(payload);

        public Task<byte[]> DownloadContentBytesAsync(
            ContentReference contentRef,
            CancellationToken cancellationToken) =>
            Task.FromResult(System.Text.Encoding.UTF8.GetBytes(payload));

        public Task<ContentReference> UploadContentAsync(
            string content,
            string contentType,
            CancellationToken cancellationToken)
        {
            var contentRef = Content(
                $"upload_{Uploads.Count}",
                contentType,
                content.Length);
            Uploads.Add(contentRef);
            return Task.FromResult(contentRef);
        }

        public Task<ContentReference> UploadContentAsync(
            byte[] bytes,
            string contentType,
            CancellationToken cancellationToken)
        {
            var contentRef = Content(
                $"upload_{Uploads.Count}",
                contentType,
                bytes.Length);
            Uploads.Add(contentRef);
            return Task.FromResult(contentRef);
        }

        public Task PublishEventAsync(
            ControllerEvent controllerEvent,
            CancellationToken cancellationToken)
        {
            PublishedEvents.Add(controllerEvent);
            return Task.CompletedTask;
        }
    }
}
