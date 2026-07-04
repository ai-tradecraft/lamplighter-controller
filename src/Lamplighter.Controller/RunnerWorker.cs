using System.Collections.Immutable;
using System.Text.Json;
using Tradecraft.Contracts.AgentRuntime.V1;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lamplighter.Controller;

internal sealed class RunnerWorker(
    IOptions<RunnerOptions> options,
    IRunnerApiClient apiClient,
    IAdapterRuntimeObserver runtimeObserver,
    RunnerCommandLoop commandLoop,
    RuntimeAdapterEventLoop eventLoop,
    ILogger<RunnerWorker> logger) : BackgroundService
{
    private readonly RunnerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Lamplighter runner {RunnerId} starting for {OrchestratorBaseUri} with controller workspace {ControllerWorkspace}.",
            _options.RunnerId,
            _options.OrchestratorBaseUri,
            _options.ControllerWorkspace);

        var heartbeatTask = RunHeartbeatLoopAsync(stoppingToken);
        var commandTask = commandLoop.RunAsync(stoppingToken);
        var eventTask = eventLoop.RunAsync(stoppingToken);
        await Task.WhenAll(heartbeatTask, commandTask, eventTask);
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        await PublishHeartbeatAsync(stoppingToken);
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PublishHeartbeatAsync(stoppingToken);
        }
    }

    private async Task PublishHeartbeatAsync(CancellationToken cancellationToken)
    {
        var inventory = await runtimeObserver.ObserveAsync(cancellationToken);
        var heartbeat = new ControllerHeartbeat(
            MessageType: ControllerMessageTypes.Heartbeat,
            ProtocolVersion: ControllerProtocolVersions.Protocol,
            SchemaVersion: ControllerProtocolVersions.Schema,
            ControllerId: _options.RunnerId,
            Status: "online",
            ObservedAt: DateTimeOffset.UtcNow,
            ActiveCommandIds: [],
            Inventory: new ControllerInventory(
                Runtimes: inventory.Runtimes.Select(runtime => ToRuntimeResource(inventory, runtime)).ToImmutableArray(),
                Sessions: inventory.Runtimes
                    .SelectMany(ToSessionResources)
                    .ToImmutableArray()),
            Extensions: ImmutableDictionary<string, JsonElement>.Empty.Add(
                "tradecraft.adapter.capabilities",
                inventory.Capabilities));

        await apiClient.UpsertHeartbeatAsync(heartbeat, cancellationToken);
        logger.LogInformation(
            "Runner heartbeat published with {AgentCount} local agents.",
            inventory.Runtimes.Count);
    }

    private AgentRuntimeResource ToRuntimeResource(
        AdapterRuntimeInventory inventory,
        AdapterRuntimeInventoryItem runtime)
    {
        return new AgentRuntimeResource(
            DocumentType: "agent_runtime",
            RuntimeId: runtime.RuntimeId,
            ControllerId: _options.RunnerId,
            Status: runtime.Status,
            AdapterKind: inventory.AdapterKind,
            DeploymentMode: inventory.DeploymentMode,
            CreatedAt: runtime.ObservedAt,
            UpdatedAt: runtime.ObservedAt,
            Extensions: ImmutableDictionary<string, JsonElement>.Empty.Add(
                "tradecraft.poc",
                JsonSerializer.SerializeToElement(new
                {
                    agent_session_id = runtime.AgentSessionId,
                    runtime_path = runtime.RuntimePath,
                    workspace_path = runtime.WorkspacePath,
                    provider_endpoint = runtime.ProviderEndpoint,
                    provider_pid = runtime.ProviderProcessId
                })));
    }

    private static IEnumerable<AgentSessionResource> ToSessionResources(
        AdapterRuntimeInventoryItem runtime)
    {
        return runtime.Sessions
            .Select(session => new AgentSessionResource(
                DocumentType: "agent_session",
                AgentSessionId: session.SessionId,
                RuntimeId: runtime.RuntimeId,
                Status: session.Status,
                CreatedAt: session.ObservedAt,
                UpdatedAt: session.ObservedAt,
                ProviderSessionRef: session.ProviderSessionRef,
                TranscriptAuthority: "provider",
                Extensions: ImmutableDictionary<string, JsonElement>.Empty.Add(
                    "tradecraft.poc",
                    JsonSerializer.SerializeToElement(new
                    {
                        runtime_path = session.RuntimePath
                    }))));
    }
}
