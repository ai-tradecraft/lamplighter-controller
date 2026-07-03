using Microsoft.Extensions.Options;
using Xunit;

namespace Lamplighter.Controller.Tests;

public sealed class AdapterRuntimeObserverTests
{
    [Fact]
    public async Task ObserveAsync_InvokesConfiguredAdapterObservationCommand()
    {
        var process = new FakeAdapterProcessRunner(new ProcessOutput(
            "uv run lamplighter-opencode observe-runtimes",
            0,
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
            ""));
        var observer = new CliAdapterRuntimeObserver(process, Options.Create(new RunnerOptions
        {
            ControllerWorkspace = "/tmp/controller",
            Adapter = OpenCodeCliAdapterOptions()
        }));

        var inventory = await observer.ObserveAsync(CancellationToken.None);

        Assert.Equal("opencode", inventory.AdapterKind);
        Assert.Equal("local_process", inventory.DeploymentMode);
        Assert.True(inventory.Capabilities.GetProperty("snapshot_capture").GetBoolean());
        Assert.Contains("observe-runtimes", process.Arguments);
        Assert.Contains("--controller-workspace", process.Arguments);
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
        var process = new FakeAdapterProcessRunner(new ProcessOutput(
            "uv run lamplighter-opencode observe-runtimes",
            1,
            "",
            "boom"));
        var observer = new CliAdapterRuntimeObserver(process, Options.Create(new RunnerOptions
        {
            ControllerWorkspace = "/tmp/controller",
            Adapter = OpenCodeCliAdapterOptions()
        }));

        var inventory = await observer.ObserveAsync(CancellationToken.None);

        Assert.Empty(inventory.Runtimes);
        Assert.Equal("unknown", inventory.AdapterKind);
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
                Operation = "adapter-operation",
                ObserveRuntimes = "observe-runtimes"
            }
        };
    }

    private sealed class FakeAdapterProcessRunner(params ProcessOutput[] outputs)
        : IAdapterProcessRunner
    {
        private readonly Queue<ProcessOutput> _outputs = new(outputs);

        public string[] Arguments { get; private set; } = [];

        public Task<ProcessOutput> RunAsync(
            string fileName,
            string[] arguments,
            CancellationToken cancellationToken)
        {
            Arguments = arguments;
            return Task.FromResult(_outputs.Dequeue());
        }
    }
}
