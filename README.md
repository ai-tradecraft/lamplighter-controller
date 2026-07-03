# Lamplighter Controller

Provider-neutral controller process for coordinating agent runtimes from the
edge of a deployment.

The controller speaks the stable northbound protocol to the Tradecraft
orchestrator and delegates provider/deployment-specific model access to runtime
adapters. The current POC adapter is `lamplighter-opencode`, invoked as a local
CLI transport from this controller.

## Boundaries

- Northbound orchestrator/controller contracts live in
  `../tradecraft-contracts/src/Tradecraft.Contracts`.
- Southbound controller/adapter protocol schemas live in
  `../tradecraft-contracts/contracts/agent-runtime/v1/schemas/runtime-adapter-message.schema.json`.
  The controller's C# `IAgentRuntimeAdapter` is an internal port that maps
  controller commands onto that provider-neutral operation model.
- `IAdapterTransport` carries those operation envelopes. The default
  `CliAdapterTransport` writes an `adapter.operation` file, invokes the
  configured adapter command, and reads an `adapter.operation_result` envelope
  back. Provider-specific commands such as OpenCode session creation remain
  inside the adapter package.
- Inline request data uses the shared envelope's top-level `payload` when the
  v1 schema supports that operation. Legacy/narrow turn input currently uses a
  local first-class `payload_ref`. Adapter results return a first-class
  `result_ref`; the controller dereferences that local handle and publishes the
  result through the orchestrator content path.
- Runtime heartbeat inventory comes from the configured adapter observation
  command. The controller no longer parses OpenCode runtime files or probes
  OpenCode health endpoints directly.
- OpenCode adapter payload schemas live in
  `../tradecraft-contracts/contracts/lamplighter-opencode/schemas`.
- The controller does not own provider/model configuration. It passes work to
  the configured adapter and keeps provider-specific secrets/configuration out
  of its own contract surface.

## Run locally

From the meta-repository `submodules` directory:

```sh
cd lamplighter-controller
Runner__Adapter__WorkingDirectory=../lamplighter-opencode \
TRADECRAFT_CONTRACTS_ROOT=../tradecraft-contracts \
dotnet run --project src/Lamplighter.Controller
```

## Verify

```sh
dotnet test Lamplighter.Controller.slnx
```
