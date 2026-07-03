using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

    public static AgentRuntimeAdapterResult Failure(AdapterTransportResult output)
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
    IAdapterTransport transport,
    IOptions<RunnerOptions> options) : IAgentRuntimeAdapter
{
    private const string ControllerWorkspaceExtensionName = "tradecraft.dev/controller_workspace";

    private readonly RunnerOptions _options = options.Value;

    public async Task<AgentRuntimeAdapterResult> StartRuntimeAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await SendOperationAsync("StartRuntime", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.start-agent-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> StopRuntimeAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await SendOperationAsync("StopRuntime", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.stop-agent-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> CreateSessionAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await SendOperationAsync("CreateSession", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.create-session-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> StartInvocationAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await SendOperationAsync("StartInvocation", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.agent-turn-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> CloseSessionAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await SendOperationAsync("CloseSession", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.cancel-session-result+json");
    }

    public async Task<AgentRuntimeAdapterResult> SynchronizeSessionHistoryAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await SendOperationAsync("ReadTranscript", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.agent-chat-history+json");
    }

    private async Task<AdapterTransportResult> SendOperationAsync(
        string operationType,
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var operation = CreateAdapterOperation(operationType, request);
        return await transport.SendAsync(operation, request.CommandId, cancellationToken)
            .ConfigureAwait(false);
    }

    private JsonObject CreateAdapterOperation(
        string operationType,
        AgentRuntimeAdapterRequest request)
    {
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
                ControllerProtocolJson.Options),
            ["extensions"] = new JsonObject
            {
                [ControllerWorkspaceExtensionName] = _options.ControllerWorkspace
            }
        };
        if (!string.IsNullOrWhiteSpace(request.Payload))
        {
            if (string.Equals(operationType, "StartInvocation", StringComparison.Ordinal))
            {
                operation["payload_ref"] = JsonSerializer.SerializeToNode(
                    CreateLocalPayloadReference(request),
                    ControllerProtocolJson.Options);
            }
            else
            {
                operation["payload"] = JsonNode.Parse(request.Payload);
            }
        }
        return operation;
    }

    private ContentReference CreateLocalPayloadReference(AgentRuntimeAdapterRequest request)
    {
        var commandRoot = Path.Combine(
            _options.ControllerWorkspace,
            "commands",
            AdapterIdentifier.Safe(request.CommandId, "cmd_"));
        Directory.CreateDirectory(commandRoot);
        var path = Path.Combine(commandRoot, "adapter-payload.json");
        var payloadBytes = Encoding.UTF8.GetBytes(request.Payload ?? "");
        File.WriteAllBytes(path, payloadBytes);
        return new ContentReference(
            new Uri(path).AbsoluteUri,
            Convert.ToHexStringLower(SHA256.HashData(payloadBytes)),
            request.PayloadContentType ?? "application/json",
            payloadBytes.Length);
    }

    private static AgentRuntimeAdapterResult Complete(
        AdapterTransportResult output,
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

            if (root.TryGetProperty("result_ref", out var resultRef))
            {
                return CompleteFromResultReference(output, resultRef, successContentType);
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

    private static AgentRuntimeAdapterResult CompleteFromResultReference(
        AdapterTransportResult output,
        JsonElement resultRef,
        string fallbackContentType)
    {
        try
        {
            var uri = resultRef.GetProperty("uri").GetString();
            var contentType = resultRef.TryGetProperty("content_type", out var contentTypeElement)
                && contentTypeElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(contentTypeElement.GetString())
                    ? contentTypeElement.GetString()!
                    : fallbackContentType;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                || !string.Equals(parsed.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            {
                return new AgentRuntimeAdapterResult(
                    false,
                    $"Runtime adapter returned unsupported result_ref URI: {uri}.",
                    "text/plain",
                    output.Command,
                    output.ExitCode,
                    output.Stdout,
                    output.Stderr);
            }

            return AgentRuntimeAdapterResult.Success(File.ReadAllText(parsed.LocalPath), contentType);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AgentRuntimeAdapterResult(
                false,
                $"Runtime adapter result_ref could not be read: {ex.Message}",
                "text/plain",
                output.Command,
                output.ExitCode,
                output.Stdout,
                output.Stderr);
        }
    }
}

internal interface IAdapterTransport
{
    Task<AdapterTransportResult> SendAsync(
        JsonObject operation,
        string operationId,
        CancellationToken cancellationToken);
}

internal sealed class CliAdapterTransport(
    IAdapterProcessRunner processRunner,
    IOptions<RunnerOptions> options) : IAdapterTransport
{
    private readonly RunnerOptions _options = options.Value;

    public async Task<AdapterTransportResult> SendAsync(
        JsonObject operation,
        string operationId,
        CancellationToken cancellationToken)
    {
        var operationPath = await WriteAdapterOperationAsync(operation, operationId, cancellationToken)
            .ConfigureAwait(false);
        var arguments = _options.Adapter.ArgumentPrefix
            .Concat([
                _options.Adapter.Commands.Operation,
                "--operation", operationPath,
                "--json"
            ])
            .ToArray();
        var output = await processRunner.RunAsync(
                _options.Adapter.Executable,
                arguments,
                cancellationToken)
            .ConfigureAwait(false);
        return new AdapterTransportResult(output.Command, output.ExitCode, output.Stdout, output.Stderr);
    }

    private async Task<string> WriteAdapterOperationAsync(
        JsonObject operation,
        string operationId,
        CancellationToken cancellationToken)
    {
        var commandRoot = Path.Combine(
            _options.ControllerWorkspace,
            "commands",
            SafeIdentifier(operationId, "cmd_"));
        Directory.CreateDirectory(commandRoot);
        var path = Path.Combine(commandRoot, "adapter-operation.json");
        await File.WriteAllTextAsync(
                path,
                operation.ToJsonString(ControllerProtocolJson.Options),
                cancellationToken)
            .ConfigureAwait(false);
        return path;
    }

    private static string SafeIdentifier(string value, string prefix)
    {
        return AdapterIdentifier.Safe(value, prefix);
    }
}

internal static class AdapterIdentifier
{
    public static string Safe(string value, string prefix)
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

internal sealed record AdapterTransportResult(string Command, int ExitCode, string Stdout, string Stderr);
