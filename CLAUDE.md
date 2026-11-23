# Claude instructions for ioBroker-NewGen.runtime

## Repository scope

This repository contains the NewGen host runtime: the state matrix, registry,
security, routing, adapter lifecycle, TCP bridge, legacy Node.js process
management, and shared adapter contracts.

## Build and validation

- Target framework: .NET 9.
- Main build: `dotnet build ioBroker-NewGen.runtime.csproj --no-restore`.
- The runtime build must succeed without warnings or errors before handing off a
  core/API change.
- Integration checks are under `tests/IntegrationTests`; do not assume they are
  compatible with unrelated historical version assertions without inspecting
  the test project first.
- Every change must be recorded in `CHANGELOG.md`. Update the relevant
  documentation under `/Doku` or `doc/` whenever behavior, APIs, configuration,
  architecture, lifecycle, or operational procedures change.

## Runtime/deployment rules

- Native adapter assemblies are loaded from `Adapters/native` in a deployment
  and from the corresponding `bin/Debug/net9.0/Adapters/native` build output.
- Do not silently replace a running adapter or runtime process. Build first,
  then clearly identify which DLLs need deployment and restart.
- The external Web Adapter owns the listener. Keep HTTP/WebSocket endpoint
  registration in the shared `IWebEndpointRegistry` contract.
- Legacy adapters run in isolated Node.js processes and communicate through the
  TCP bridge; preserve instance IDs such as `iot.0` exactly.

## Core contract changes

When changing a public runtime contract or a class consumed by Admin/Web/native
adapter repositories:

1. Build the runtime.
2. Copy the fresh runtime contract DLL and any updated dependency resources into
   each affected repository's `/lib` directory, replacing stale copies.
3. Rebuild each dependent repository.
4. Validate the deployed adapter paths and restart requirements.

Avoid editing generated `bin`/`obj` output as a substitute for changing source.
