using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tradecraft.Contracts.AgentRuntime.V1;

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
    ResourceTarget Target,
    string IdempotencyKey,
    DateTimeOffset Deadline,
    long FencingToken,
    ProtocolCorrelation Correlation,
    AuthorizationContext AuthorizationContext,
    string? Payload = null,
    string? PayloadContentType = null)
{
    public string? RuntimeId => Target.RuntimeId;

    public string? SessionId => Target.AgentSessionId;

    public string? InvocationId => Target.InvocationId;
}

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
    private const string PayloadExtensionName = "tradecraft.dev/payload";
    private const string PayloadContentTypeExtensionName = "tradecraft.dev/payload_content_type";
    private const string ControllerWorkspaceExtensionName = "tradecraft.dev/controller_workspace";

    private readonly RunnerOptions _options = options.Value;

    public async Task<AgentRuntimeAdapterResult> StartRuntimeAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await RunOperationAsync("StartRuntime", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.start-agent-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> StopRuntimeAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await RunOperationAsync("StopRuntime", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.stop-agent-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> CreateSessionAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await RunOperationAsync("CreateSession", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.create-session-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> StartInvocationAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await RunOperationAsync("StartInvocation", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.agent-turn-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> CloseSessionAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await RunOperationAsync("CloseSession", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.cancel-session-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> SynchronizeSessionHistoryAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await RunOperationAsync("ReadTranscript", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.agent-chat-history+json");
    }

    private async Task<ProcessOutput> RunOperationAsync(
        string operationType,
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var operationPath = await WriteAdapterOperationAsync(
                operationType,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        var arguments = _options.Adapter.ArgumentPrefix
            .Concat([
                _options.Adapter.Commands.Operation,
                "--operation", operationPath,
                "--json"
            ])
            .ToArray();
        return await processRunner.RunAsync(
                _options.Adapter.Executable,
                arguments,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> WriteAdapterOperationAsync(
        string operationType,
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var commandRoot = Path.Combine(
            _options.ControllerWorkspace,
            "commands",
            SafeIdentifier(request.CommandId, "cmd_"));
        Directory.CreateDirectory(commandRoot);
        var path = Path.Combine(commandRoot, "adapter-operation.json");
        var operation = new JsonObject
        {
            ["message_type"] = "adapter.operation",
            ["protocol_version"] = ControllerProtocolVersions.Protocol,
            ["schema_version"] = ControllerProtocolVersions.Schema,
            ["operation_id"] = request.CommandId,
            ["operation_type"] = operationType,
            ["idempotency_key"] = request.IdempotencyKey,
            ["target"] = JsonSerializer.SerializeToNode(request.Target, ControllerProtocolJson.Options),
            ["deadline"] = request.Deadline.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["fencing_token"] = request.FencingToken,
            ["correlation"] = JsonSerializer.SerializeToNode(request.Correlation, ControllerProtocolJson.Options),
            ["authorization_context"] = JsonSerializer.SerializeToNode(
                request.AuthorizationContext,
                ControllerProtocolJson.Options)
        };
        var extensions = new JsonObject
        {
            [ControllerWorkspaceExtensionName] = _options.ControllerWorkspace
        };
        if (!string.IsNullOrWhiteSpace(request.Payload))
        {
            extensions[PayloadExtensionName] = JsonNode.Parse(request.Payload);
            if (!string.IsNullOrWhiteSpace(request.PayloadContentType))
            {
                extensions[PayloadContentTypeExtensionName] = request.PayloadContentType;
            }
        }
        operation["extensions"] = extensions;
        await File.WriteAllTextAsync(
                path,
                operation.ToJsonString(ControllerProtocolJson.Options),
                cancellationToken)
            .ConfigureAwait(false);
        return path;
    }

    private static AgentRuntimeAdapterResult Complete(
        ProcessOutput output,
        string successContentType)
    {
        if (output.ExitCode != 0)
        {
            return AgentRuntimeAdapterResult.Failure(output);
        }

        try
        {
            using var result = JsonDocument.Parse(output.Stdout);
            var root = result.RootElement;
            var status = root.GetProperty("status").GetString();
            if (!string.Equals(status, "completed", StringComparison.Ordinal))
            {
                var summary = root.TryGetProperty("error", out var error)
                    && error.TryGetProperty("summary", out var summaryElement)
                    && summaryElement.ValueKind == JsonValueKind.String
                        ? summaryElement.GetString()
                        : "Runtime adapter operation failed.";
                return new AgentRuntimeAdapterResult(
                    false,
                    summary ?? "Runtime adapter operation failed.",
                    "text/plain",
                    output.Command,
                    output.ExitCode,
                    output.Stdout,
                    output.Stderr);
            }

            if (root.TryGetProperty("extensions", out var extensions)
                && extensions.TryGetProperty(PayloadExtensionName, out var payload))
            {
                var contentType = successContentType;
                if (extensions.TryGetProperty(PayloadContentTypeExtensionName, out var contentTypeElement)
                    && contentTypeElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(contentTypeElement.GetString()))
                {
                    contentType = contentTypeElement.GetString()!;
                }

                return AgentRuntimeAdapterResult.Success(payload.GetRawText(), contentType);
            }

            return AgentRuntimeAdapterResult.Success(output.Stdout, "application/vnd.tradecraft.adapter-operation-result+json");
        }
        catch (JsonException ex)
        {
            return new AgentRuntimeAdapterResult(
                false,
                $"Runtime adapter returned invalid adapter.operation_result JSON: {ex.Message}",
                "text/plain",
                output.Command,
                output.ExitCode,
                output.Stdout,
                output.Stderr);
        }
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
            UseShellExecute = false,
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
