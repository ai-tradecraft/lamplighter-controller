using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lamplighter.Controller;

internal interface IAgentRuntimeAdapter
{
    Task<AgentRuntimeAdapterResult> StartRuntimeAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken);

    Task<AgentRuntimeAdapterResult> StopRuntimeAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken);

    Task<AgentRuntimeAdapterResult> CreateSessionAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken);

    Task<AgentRuntimeAdapterResult> StartInvocationAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken);

    Task<AgentRuntimeAdapterResult> CloseSessionAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken);

    Task<AgentRuntimeAdapterResult> SynchronizeSessionHistoryAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken);
}

internal sealed record AgentRuntimeAdapterRequest(
    string CommandId,
    string? RuntimeId = null,
    string? SessionId = null,
    string? InvocationId = null,
    string? Payload = null);

internal sealed record AgentRuntimeAdapterResult(
    bool Succeeded,
    string Payload,
    string ContentType,
    string? Command = null,
    int? ExitCode = null,
    string? StandardOutput = null,
    string? StandardError = null)
{
    public static AgentRuntimeAdapterResult Success(string payload, string contentType)
    {
        return new AgentRuntimeAdapterResult(true, payload, contentType);
    }

    public static AgentRuntimeAdapterResult Failure(ProcessOutput output)
    {
        var diagnostics = $"""
            command: {output.Command}
            exit_code: {output.ExitCode}

            stdout:
            {output.Stdout}

            stderr:
            {output.Stderr}
            """;
        return new AgentRuntimeAdapterResult(
            false,
            diagnostics,
            "text/plain",
            output.Command,
            output.ExitCode,
            output.Stdout,
            output.Stderr);
    }
}

internal sealed class CliAgentRuntimeAdapter(
    IAdapterProcessRunner processRunner,
    IOptions<RunnerOptions> options) : IAgentRuntimeAdapter
{
    private readonly RunnerOptions _options = options.Value;

    public async Task<AgentRuntimeAdapterResult> StartRuntimeAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var runtimeId = Required(request.RuntimeId, "runtime_id");
        var specPath = await WriteCommandPayloadAsync(request, "agent-spec.json", cancellationToken);
        var prepareOutput = await RunAsync(
            [
                _options.Adapter.Commands.PrepareRuntime,
                "--spec", specPath,
                "--controller-workspace", _options.ControllerWorkspace,
                "--json"
            ],
            cancellationToken);
        if (prepareOutput.ExitCode != 0)
        {
            return AgentRuntimeAdapterResult.Failure(prepareOutput);
        }

        var startOutput = await RunAsync(
            [
                _options.Adapter.Commands.StartRuntime,
                "--agent", runtimeId,
                "--controller-workspace", _options.ControllerWorkspace,
                "--json"
            ],
            cancellationToken);
        return Complete(
            startOutput,
            "application/vnd.tradecraft.start-agent-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> StopRuntimeAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await RunAsync(
            [
                _options.Adapter.Commands.StopRuntime,
                "--agent", Required(request.RuntimeId, "runtime_id"),
                "--controller-workspace", _options.ControllerWorkspace,
                "--json"
            ],
            cancellationToken);
        return Complete(output, "application/vnd.tradecraft.stop-agent-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> CreateSessionAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var specPath = await WriteCommandPayloadAsync(request, "session-spec.json", cancellationToken);
        var output = await RunAsync(
            [
                _options.Adapter.Commands.CreateSession,
                "--agent", Required(request.RuntimeId, "runtime_id"),
                "--spec", specPath,
                "--controller-workspace", _options.ControllerWorkspace,
                "--json"
            ],
            cancellationToken);
        return Complete(output, "application/vnd.tradecraft.create-session-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> StartInvocationAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var sessionId = Required(request.SessionId, "session_id");
        var runtimeId = request.RuntimeId;
        var sessionRoot = runtimeId is null
            ? SessionRoot(sessionId)
            : AgentSessionRoot(runtimeId, sessionId);
        Directory.CreateDirectory(sessionRoot);
        var requestPath = Path.Combine(
            sessionRoot,
            $"{Required(request.InvocationId, "invocation_id")}.request.json");
        await File.WriteAllTextAsync(
                requestPath,
                Required(request.Payload, "payload"),
                cancellationToken)
            .ConfigureAwait(false);

        var arguments = new List<string>
        {
            _options.Adapter.Commands.StartInvocation,
            "--session", sessionId,
            "--request", requestPath
        };
        if (runtimeId is not null)
        {
            arguments.AddRange([
                "--agent", runtimeId,
                "--controller-workspace", _options.ControllerWorkspace
            ]);
        }
        arguments.Add("--json");

        var output = await RunAsync([.. arguments], cancellationToken);
        return Complete(output, "application/vnd.tradecraft.agent-turn-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> CloseSessionAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await RunAsync(
            [
                _options.Adapter.Commands.CloseSession,
                "--agent", Required(request.RuntimeId, "runtime_id"),
                "--session", Required(request.SessionId, "session_id"),
                "--controller-workspace", _options.ControllerWorkspace,
                "--json"
            ],
            cancellationToken);
        return Complete(output, "application/vnd.tradecraft.cancel-session-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> SynchronizeSessionHistoryAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await RunAsync(
            [
                _options.Adapter.Commands.SynchronizeSessionHistory,
                "--agent", Required(request.RuntimeId, "runtime_id"),
                "--session", Required(request.SessionId, "session_id"),
                "--controller-workspace", _options.ControllerWorkspace,
                "--json"
            ],
            cancellationToken);
        return Complete(output, "application/vnd.tradecraft.agent-chat-history+json");
    }

    private async Task<ProcessOutput> RunAsync(
        string[] operationArguments,
        CancellationToken cancellationToken)
    {
        var arguments = _options.Adapter.ArgumentPrefix
            .Concat(operationArguments)
            .ToArray();
        return await processRunner.RunAsync(
                _options.Adapter.Executable,
                arguments,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> WriteCommandPayloadAsync(
        AgentRuntimeAdapterRequest request,
        string fileName,
        CancellationToken cancellationToken)
    {
        var commandRoot = Path.Combine(
            _options.ControllerWorkspace,
            "commands",
            SafeIdentifier(request.CommandId, "cmd_"));
        Directory.CreateDirectory(commandRoot);
        var path = Path.Combine(commandRoot, fileName);
        await File.WriteAllTextAsync(
                path,
                Required(request.Payload, "payload"),
                cancellationToken)
            .ConfigureAwait(false);
        return path;
    }

    private string SessionRoot(string sessionId)
    {
        return Path.Combine(_options.ControllerWorkspace, "sessions", sessionId);
    }

    private string AgentSessionRoot(string agentId, string sessionId)
    {
        return Path.Combine(
            _options.ControllerWorkspace,
            "agents",
            SafeIdentifier(agentId, "agent_"),
            "runtime",
            "sessions",
            SafeIdentifier(sessionId, "session_"));
    }

    private static AgentRuntimeAdapterResult Complete(
        ProcessOutput output,
        string successContentType)
    {
        return output.ExitCode == 0
            ? AgentRuntimeAdapterResult.Success(output.Stdout, successContentType)
            : AgentRuntimeAdapterResult.Failure(output);
    }

    private static string Required(string? value, string name)
    {
        return value ?? throw new InvalidOperationException($"Adapter request is missing {name}.");
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

internal interface IAdapterProcessRunner
{
    Task<ProcessOutput> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken);
}

internal sealed class CliAdapterProcessRunner(
    IOptions<RunnerOptions> options,
    ILogger<CliAdapterProcessRunner> logger) : IAdapterProcessRunner
{
    private static readonly string[] ProviderOwnedEnvironmentVariables =
    [
        "LAMPLIGHTER_OPENCODE_CONFIG_MODE",
        "LAMPLIGHTER_OPENCODE_MODEL",
        "OPENCODE_MODEL",
        "OPENCODE_CONFIG",
        "OPENCODE_CONFIG_DIR",
        "OPENCODE_CONFIG_CONTENT",
        "AZURE_OPENAI_API_KEY",
        "AZURE_OPENAI_ENDPOINT",
        "AZURE_OPENAI_DEPLOYMENT",
        "AZURE_OPENAI_RESOURCE_NAME",
        "ANTHROPIC_API_KEY",
        "OPENAI_API_KEY"
    ];

    private readonly RunnerOptions _options = options.Value;

    public async Task<ProcessOutput> RunAsync(
        string fileName,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var command = string.Join(" ", new[] { fileName }.Concat(arguments.Select(Quote)));
        var startedAt = Stopwatch.GetTimestamp();
        logger.LogInformation("Starting runtime adapter process: {Command}", command);
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = LocateAdapterWorkingDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConfigureAdapterProcessEnvironment(startInfo.Environment);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = new ProcessOutput(command, process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        if (output.ExitCode == 0)
        {
            logger.LogInformation(
                "Runtime adapter process completed with exit code 0 in {ElapsedMilliseconds} ms.",
                elapsed.TotalMilliseconds);
        }
        else
        {
            logger.LogWarning(
                "Runtime adapter process failed with exit code {ExitCode} in {ElapsedMilliseconds} ms; stderr length {StderrLength}.",
                output.ExitCode,
                elapsed.TotalMilliseconds,
                output.Stderr.Length);
        }

        return output;
    }

    private string LocateAdapterWorkingDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.Adapter.WorkingDirectory))
        {
            return Path.GetFullPath(_options.Adapter.WorkingDirectory);
        }

        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "pyproject.toml")) && Directory.Exists(Path.Combine(current, "src", "lamplighter_opencode")))
            {
                return current;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent == current)
            {
                break;
            }
            current = parent ?? "";
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    }

    internal static void ConfigureAdapterProcessEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var name in ProviderOwnedEnvironmentVariables)
        {
            environment.Remove(name);
        }
    }

    private static string Quote(string value)
    {
        return value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
    }
}

internal sealed record ProcessOutput(string Command, int ExitCode, string Stdout, string Stderr);
