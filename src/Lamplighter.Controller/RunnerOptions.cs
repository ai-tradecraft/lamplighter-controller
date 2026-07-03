namespace Lamplighter.Controller;

internal sealed record RunnerOptions
{
    public string RunnerId { get; init; } = $"runner_{Environment.MachineName.ToLowerInvariant()}";
    public Uri OrchestratorBaseUri { get; init; } = new("http://127.0.0.1:5087");
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan LongPollWait { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
    public int LeaseSeconds { get; init; } = 60;
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromSeconds(5);
    public string ControllerWorkspace { get; set; } = "";
    public RuntimeAdapterOptions Adapter { get; init; } = new();
}

internal sealed record RuntimeAdapterOptions
{
    public string Kind { get; init; } = "cli";
    public string Executable { get; init; } = "";
    public string[] ArgumentPrefix { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public RuntimeAdapterCommandOptions Commands { get; init; } = new();
}

internal sealed record RuntimeAdapterCommandOptions
{
    public string Operation { get; init; } = "adapter-operation";
    public string ObserveRuntimes { get; init; } = "observe-runtimes";
}
