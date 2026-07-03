using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Tradecraft.Contracts.AgentRuntime.V1;

namespace Lamplighter.Controller;

internal interface IAdapterRuntimeObserver
{
    Task<AdapterRuntimeInventory> ObserveAsync(CancellationToken cancellationToken);
}

internal sealed record AdapterRuntimeInventory(
    string AdapterKind,
    string AdapterVersion,
    string DeploymentMode,
    DateTimeOffset ObservedAt,
    JsonElement Capabilities,
    IReadOnlyList<AdapterRuntimeInventoryItem> Runtimes);

internal sealed record AdapterRuntimeInventoryItem(
    string RuntimeId,
    string AgentSessionId,
    string Status,
    string RuntimePath,
    string WorkspacePath,
    DateTimeOffset ObservedAt,
    string? ProviderEndpoint,
    int? ProviderProcessId,
    IReadOnlyList<AdapterRuntimeSessionInventoryItem> Sessions);

internal sealed record AdapterRuntimeSessionInventoryItem(
    string SessionId,
    string Status,
    string RuntimePath,
    string? ProviderSessionRef,
    DateTimeOffset ObservedAt);

internal sealed class CliAdapterRuntimeObserver(
    IAgentRuntimeAdapter runtimeAdapter,
    IOptions<RunnerOptions> options) : IAdapterRuntimeObserver
{
    private readonly RunnerOptions _options = options.Value;

    public async Task<AdapterRuntimeInventory> ObserveAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var output = await runtimeAdapter.InspectRuntimeAsync(
                new AgentRuntimeAdapterRequest(
                    CommandId: "cmd_runtime_inspect",
                    Target: new ResourceTarget(ControllerId: _options.RunnerId),
                    IdempotencyKey: $"runtime-inspect:{_options.RunnerId}",
                    Deadline: now.Add(_options.HeartbeatInterval),
                    FencingToken: 1,
                    Correlation: new ProtocolCorrelation(
                        CommandId: "cmd_runtime_inspect",
                        CorrelationId: $"runtime-inspect:{_options.RunnerId}"),
                    AuthorizationContext: new AuthorizationContext(
                        "system://lamplighter-controller",
                        $"authorization-grant://controller/{Uri.EscapeDataString(_options.RunnerId)}",
                        now)),
                cancellationToken)
            .ConfigureAwait(false);
        if (!output.Succeeded)
        {
            return EmptyInventory(DateTimeOffset.UtcNow);
        }

        try
        {
            return ParseInventory(output.Payload);
        }
        catch (JsonException)
        {
            return EmptyInventory(DateTimeOffset.UtcNow);
        }
    }

    private static AdapterRuntimeInventory ParseInventory(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var observedAt = ReadDateTime(root, "observed_at") ?? DateTimeOffset.UtcNow;
        var runtimes = root.TryGetProperty("runtimes", out var runtimeArray)
            && runtimeArray.ValueKind == JsonValueKind.Array
                ? runtimeArray.EnumerateArray().Select(ReadRuntime).OfType<AdapterRuntimeInventoryItem>().ToArray()
                : [];
        var capabilities = root.TryGetProperty("capabilities", out var capabilitiesElement)
            ? capabilitiesElement.Clone()
            : JsonSerializer.SerializeToElement(new { });
        return new AdapterRuntimeInventory(
            ReadString(root, "adapter_kind") ?? "unknown",
            ReadString(root, "adapter_version") ?? "unknown",
            ReadString(root, "deployment_mode") ?? "unknown",
            observedAt,
            capabilities,
            runtimes);
    }

    private static AdapterRuntimeInventoryItem? ReadRuntime(JsonElement runtime)
    {
        var runtimeId = ReadString(runtime, "runtime_id");
        if (string.IsNullOrWhiteSpace(runtimeId))
        {
            return null;
        }
        var observedAt = ReadDateTime(runtime, "observed_at") ?? DateTimeOffset.UtcNow;
        var sessions = runtime.TryGetProperty("sessions", out var sessionArray)
            && sessionArray.ValueKind == JsonValueKind.Array
                ? sessionArray.EnumerateArray().Select(ReadSession).OfType<AdapterRuntimeSessionInventoryItem>().ToArray()
                : [];
        return new AdapterRuntimeInventoryItem(
            RuntimeId: runtimeId,
            AgentSessionId: ReadString(runtime, "agent_session_id") ?? runtimeId,
            Status: ReadString(runtime, "status") ?? "unknown",
            RuntimePath: ReadString(runtime, "runtime_path") ?? "",
            WorkspacePath: ReadString(runtime, "workspace_path") ?? "",
            ObservedAt: observedAt,
            ProviderEndpoint: ReadString(runtime, "provider_endpoint"),
            ProviderProcessId: ReadInt(runtime, "provider_pid"),
            Sessions: sessions);
    }

    private static AdapterRuntimeSessionInventoryItem? ReadSession(JsonElement session)
    {
        var sessionId = ReadString(session, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }
        return new AdapterRuntimeSessionInventoryItem(
            SessionId: sessionId,
            Status: ReadString(session, "status") ?? "unknown",
            RuntimePath: ReadString(session, "runtime_path") ?? "",
            ProviderSessionRef: ReadString(session, "provider_session_ref"),
            ObservedAt: ReadDateTime(session, "observed_at") ?? DateTimeOffset.UtcNow);
    }

    private static AdapterRuntimeInventory EmptyInventory(DateTimeOffset observedAt)
    {
        return new AdapterRuntimeInventory(
            "unknown",
            "unknown",
            "unknown",
            observedAt,
            JsonSerializer.SerializeToElement(new { }),
            []);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;
    }

    private static DateTimeOffset? ReadDateTime(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var dateTime)
                ? dateTime
                : null;
    }
}
