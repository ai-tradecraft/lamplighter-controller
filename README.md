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
- Controller-built operations and adapter results are validated against the
  runtime-adapter envelope invariants before they cross the controller boundary.
- Invocation input is normalized into the v1 `invocation_input` payload shape.
  The current OpenCode POC still receives its narrow legacy turn request through
  a namespaced content reference inside that payload's `extensions`.
- Adapter results return a first-class `result_ref`; the controller dereferences
  that local handle and publishes the result through the orchestrator content
  path.
- Runtime heartbeat inventory is requested through the same adapter operation
  path using `InspectRuntime`. The controller no longer parses OpenCode runtime
  files or probes OpenCode health endpoints directly.
- Runtime adapter events are consumed through the same provider-neutral
  operation path using `ReadEvents`. The controller event-forwarding loop maps
  adapter events to northbound controller events, preserves the source adapter
  event in event extensions, and advances its local replay cursor only after
  publishing a page.
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
