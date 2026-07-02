# Lamplighter Controller

Provider-neutral controller process for coordinating agent runtimes from the
edge of a deployment.

The controller speaks the stable northbound protocol to the Tradecraft
orchestrator and delegates provider/deployment-specific model access to runtime
adapters. The current POC adapter is `lamplighter-opencode`, invoked as a local
CLI from this controller.

## Boundaries

- Northbound orchestrator/controller contracts live in
  `../tradecraft-contracts/src/Tradecraft.Contracts`.
- Southbound controller/adapter protocol schemas live in
  `../tradecraft-contracts/contracts/agent-runtime/v1/schemas/runtime-adapter-message.schema.json`.
  The controller's C# `IAgentRuntimeAdapter` is an internal port that maps
  controller commands onto that provider-neutral operation model.
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
