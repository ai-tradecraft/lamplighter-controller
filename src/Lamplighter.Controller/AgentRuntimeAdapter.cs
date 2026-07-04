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
    Task<AgentRuntimeAdapterResult> InspectRuntimeAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken);

    Task<AgentRuntimeAdapterResult> ReadEventsAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken);

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
    private const string LegacyTurnRequestExtensionName = "tradecraft.dev/legacy_turn_request_ref";

    private readonly RunnerOptions _options = options.Value;

    public async Task<AgentRuntimeAdapterResult> InspectRuntimeAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await SendOperationAsync("InspectRuntime", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.runtime-inventory+json");
    }

    public async Task<AgentRuntimeAdapterResult> ReadEventsAsync(
        AgentRuntimeAdapterRequest request,
        CancellationToken cancellationToken)
    {
        var output = await SendOperationAsync("ReadEvents", request, cancellationToken)
            .ConfigureAwait(false);
        return Complete(output, "application/vnd.tradecraft.adapter-event-batch+json");
    }

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
                operation["payload"] = CreateInvocationInputPayload(request);
            }
            else
            {
                operation["payload"] = JsonNode.Parse(request.Payload);
            }
        }
        RuntimeAdapterEnvelopeValidator.ValidateOperation(operation);
        return operation;
    }

    private JsonObject CreateInvocationInputPayload(AgentRuntimeAdapterRequest request)
    {
        var legacyTurnRequestRef = CreateLocalContentReference(
            request,
            "legacy-turn-request.json",
            request.Payload ?? "",
            request.PayloadContentType ?? "application/json");
        var instruction = TryReadInstruction(request.Payload);
        var instructionRef = CreateLocalContentReference(
            request,
            "instruction.md",
            instruction,
            "text/markdown");
        return new JsonObject
        {
            ["document_type"] = "invocation_input",
            ["invocation_id"] = request.InvocationId ?? request.CommandId,
            ["agent_spec_ref"] = $"agent-spec://{Uri.EscapeDataString(request.RuntimeId ?? "unknown")}",
            ["context_package_ref"] = $"context-package://{Uri.EscapeDataString(request.CommandId)}",
            ["instruction_ref"] = JsonSerializer.SerializeToNode(instructionRef, ControllerProtocolJson.Options),
            ["completion_contract_ref"] = "completion-contract://tradecraft/default",
            ["deadline"] = request.Deadline.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["extensions"] = new JsonObject
            {
                [LegacyTurnRequestExtensionName] = JsonSerializer.SerializeToNode(
                    legacyTurnRequestRef,
                    ControllerProtocolJson.Options)
            }
        };
    }

    private ContentReference CreateLocalContentReference(
        AgentRuntimeAdapterRequest request,
        string fileName,
        string content,
        string contentType)
    {
        var commandRoot = Path.Combine(
            _options.ControllerWorkspace,
            "commands",
            AdapterIdentifier.Safe(request.CommandId, "cmd_"));
        Directory.CreateDirectory(commandRoot);
        var path = Path.Combine(commandRoot, fileName);
        var contentBytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, contentBytes);
        return new ContentReference(
            new Uri(path).AbsoluteUri,
            Convert.ToHexStringLower(SHA256.HashData(contentBytes)),
            contentType,
            contentBytes.Length);
    }

    private static string TryReadInstruction(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("instruction", out var instruction)
                && instruction.ValueKind == JsonValueKind.String
                    ? instruction.GetString() ?? ""
                    : "";
        }
        catch (JsonException)
        {
            return "";
        }
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
            RuntimeAdapterEnvelopeValidator.ValidateResult(root);
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

internal static class RuntimeAdapterEnvelopeValidator
{
    private static readonly HashSet<string> OperationTypes = new(StringComparer.Ordinal)
    {
        "DescribeAdapter",
        "ValidateRuntimeSpec",
        "CheckReadiness",
        "PrepareRuntime",
        "StartRuntime",
        "InspectRuntime",
        "ReadEvents",
        "StopRuntime",
        "TerminateRuntime",
        "ReconcileRuntime",
        "CreateSession",
        "InspectSession",
        "CloseSession",
        "ReadTranscript",
        "PrepareInvocation",
        "StartInvocation",
        "SendInvocationInput",
        "PauseInvocation",
        "ResumeInvocation",
        "CancelInvocation",
        "TerminateInvocation",
        "InspectInvocation",
        "OpenInteractionChannel",
        "SendInteractionInput",
        "AcknowledgeInteractionMessage",
        "CloseInteractionChannel",
        "CreateSnapshot",
        "RestoreSnapshot",
        "CollectArtifacts",
        "CollectDiagnostics"
    };

    public static void ValidateOperation(JsonObject operation)
    {
        RequireString(operation, "message_type", "adapter.operation");
        RequireString(operation, "protocol_version", ControllerProtocolVersions.Protocol);
        RequireString(operation, "schema_version", ControllerProtocolVersions.Schema);
        RequireString(operation, "operation_id");
        var operationType = RequireString(operation, "operation_type");
        if (!OperationTypes.Contains(operationType))
        {
            throw new InvalidOperationException($"Unsupported adapter operation_type: {operationType}");
        }
        RequireString(operation, "idempotency_key");
        RequireObject(operation, "target");
        RequireString(operation, "deadline");
        RequireNumber(operation, "fencing_token");
        RequireObject(operation, "correlation");
        RequireObject(operation, "authorization_context");
        if (operation.ContainsKey("payload") && operation.ContainsKey("payload_ref"))
        {
            throw new InvalidOperationException("Adapter operation cannot contain both payload and payload_ref.");
        }
        if (operation.TryGetPropertyValue("extensions", out var extensions)
            && extensions is JsonObject extensionObject
            && extensionObject.ContainsKey("tradecraft.dev/payload"))
        {
            throw new InvalidOperationException("Adapter operation must not use legacy tradecraft.dev/payload extension.");
        }
        if (string.Equals(operationType, "StartInvocation", StringComparison.Ordinal))
        {
            ValidateInvocationInputPayload(operation);
        }
    }

    public static void ValidateResult(JsonElement result)
    {
        RequireString(result, "message_type", "adapter.operation_result");
        RequireString(result, "protocol_version", ControllerProtocolVersions.Protocol);
        RequireString(result, "schema_version", ControllerProtocolVersions.Schema);
        RequireString(result, "result_id");
        RequireString(result, "operation_id");
        var status = RequireString(result, "status");
        if (status is not "completed" and not "failed" and not "cancelled")
        {
            throw new JsonException($"Unsupported adapter operation_result status: {status}");
        }
        RequireString(result, "completed_at");
        RequireNumber(result, "fencing_token");
        RequireObject(result, "correlation");
        if (status == "failed")
        {
            RequireObject(result, "error");
        }
        if (status == "completed" && !result.TryGetProperty("result_ref", out _))
        {
            throw new JsonException("Completed adapter operation_result must include result_ref.");
        }
    }

    private static void ValidateInvocationInputPayload(JsonObject operation)
    {
        if (!operation.TryGetPropertyValue("payload", out var payload) || payload is not JsonObject payloadObject)
        {
            throw new InvalidOperationException("StartInvocation must contain a v1 invocation_input payload.");
        }
        RequireString(payloadObject, "document_type", "invocation_input");
        RequireString(payloadObject, "invocation_id");
        RequireString(payloadObject, "agent_spec_ref");
        RequireString(payloadObject, "context_package_ref");
        RequireObject(payloadObject, "instruction_ref");
        RequireString(payloadObject, "completion_contract_ref");
    }

    private static string RequireString(JsonObject value, string propertyName, string? expected = null)
    {
        if (!value.TryGetPropertyValue(propertyName, out var node)
            || node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var actual)
            || string.IsNullOrWhiteSpace(actual))
        {
            throw new InvalidOperationException($"Adapter operation {propertyName} must be a non-empty string.");
        }
        if (expected is not null && !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Adapter operation {propertyName} must be {expected}.");
        }
        return actual;
    }

    private static string RequireString(JsonElement value, string propertyName, string? expected = null)
    {
        if (!value.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new JsonException($"Adapter operation_result {propertyName} must be a non-empty string.");
        }
        var actual = element.GetString()!;
        if (expected is not null && !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new JsonException($"Adapter operation_result {propertyName} must be {expected}.");
        }
        return actual;
    }

    private static void RequireObject(JsonObject value, string propertyName)
    {
        if (!value.TryGetPropertyValue(propertyName, out var node) || node is not JsonObject)
        {
            throw new InvalidOperationException($"Adapter operation {propertyName} must be an object.");
        }
    }

    private static void RequireObject(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Adapter operation_result {propertyName} must be an object.");
        }
    }

    private static void RequireNumber(JsonObject value, string propertyName)
    {
        if (!value.TryGetPropertyValue(propertyName, out var node)
            || node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<long>(out var number)
            || number < 1)
        {
            throw new InvalidOperationException($"Adapter operation {propertyName} must be a positive integer.");
        }
    }

    private static void RequireNumber(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out var number)
            || number < 1)
        {
            throw new JsonException($"Adapter operation_result {propertyName} must be a positive integer.");
        }
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

        return Environment.CurrentDirectory;
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
