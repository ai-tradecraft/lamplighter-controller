using Microsoft.Extensions.Options;
using Xunit;

namespace Lamplighter.Controller.Tests;

public sealed class AdapterRuntimeObserverTests
{
    [Fact]
    public async Task ObserveAsync_UsesRuntimeAdapterInspectRuntimeOperation()
    {
        var adapter = new FakeRuntimeAdapter(new AgentRuntimeAdapterResult(
            true,
            """
            {
              "adapter_kind": "opencode",
              "adapter_version": "0.1.0",
              "deployment_mode": "local_process",
              "observed_at": "2026-07-02T00:00:00Z",
              "capabilities": {
                "snapshot_capture": true,
                "transfer_profiles": ["local-content-handle"]
              },
              "runtimes": [
                {
                  "runtime_id": "agent_1",
                  "agent_session_id": "agent_1",
                  "status": "ready",
                  "runtime_path": "/tmp/agent_1/runtime",
                  "workspace_path": "/tmp/agent_1/workspace",
                  "observed_at": "2026-07-02T00:00:00Z",
                  "provider_endpoint": "http://127.0.0.1:4097",
                  "provider_pid": 123,
                  "sessions": [
                    {
                      "session_id": "session_1",
                      "status": "ready",
                      "runtime_path": "/tmp/agent_1/runtime/sessions/session_1",
                      "provider_session_ref": "oc_session_1",
                      "observed_at": "2026-07-02T00:00:00Z"
                    }
                  ]
                }
              ]
            }
            """,
            "application/vnd.tradecraft.runtime-inventory+json"));
        var observer = new CliAdapterRuntimeObserver(adapter, Options.Create(new RunnerOptions
        {
            RunnerId = "controller_1",
            ControllerWorkspace = "/tmp/controller",
            Adapter = new RuntimeAdapterOptions()
        }));

        var inventory = await observer.ObserveAsync(CancellationToken.None);

        Assert.Equal(1, adapter.InspectRuntimeCallCount);
        Assert.Equal("cmd_runtime_inspect", adapter.LastRequest?.CommandId);
        Assert.Equal("controller_1", adapter.LastRequest?.Target.ControllerId);
        Assert.Equal("opencode", inventory.AdapterKind);
        Assert.Equal("local_process", inventory.DeploymentMode);
        Assert.True(inventory.Capabilities.GetProperty("snapshot_capture").GetBoolean());
        var runtime = Assert.Single(inventory.Runtimes);
        Assert.Equal("agent_1", runtime.RuntimeId);
        Assert.Equal("ready", runtime.Status);
        Assert.Equal("http://127.0.0.1:4097", runtime.ProviderEndpoint);
        var session = Assert.Single(runtime.Sessions);
        Assert.Equal("session_1", session.SessionId);
        Assert.Equal("oc_session_1", session.ProviderSessionRef);
    }

    [Fact]
    public async Task ObserveAsync_WhenAdapterFails_ReturnsEmptyInventory()
    {
        var adapter = new FakeRuntimeAdapter(new AgentRuntimeAdapterResult(false, "boom", "text/plain"));
        var observer = new CliAdapterRuntimeObserver(adapter, Options.Create(new RunnerOptions
        {
            RunnerId = "controller_1",
            ControllerWorkspace = "/tmp/controller",
            Adapter = new RuntimeAdapterOptions()
        }));

        var inventory = await observer.ObserveAsync(CancellationToken.None);

        Assert.Empty(inventory.Runtimes);
        Assert.Equal("unknown", inventory.AdapterKind);
    }

    private sealed class FakeRuntimeAdapter(AgentRuntimeAdapterResult result) : IAgentRuntimeAdapter
    {
        public int InspectRuntimeCallCount { get; private set; }

        public AgentRuntimeAdapterRequest? LastRequest { get; private set; }

        public Task<AgentRuntimeAdapterResult> InspectRuntimeAsync(
            AgentRuntimeAdapterRequest request,
            CancellationToken cancellationToken)
        {
            InspectRuntimeCallCount++;
            LastRequest = request;
            return Task.FromResult(result);
        }

        public Task<AgentRuntimeAdapterResult> ReadEventsAsync(
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
    }
}
