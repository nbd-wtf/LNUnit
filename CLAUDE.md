# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

LNUnit is a unit-testing framework for Bitcoin Lightning Network systems. It provides infrastructure to write tests for Lightning Network applications by spinning up containerized Bitcoin and Lightning nodes (LND, Eclair, CLN) in Docker environments.

The repository contains:
- **LNUnit**: Core framework for managing Docker-based Lightning Network test environments
- **LNUnit.LND**: LND gRPC client wrappers and typed clients (generated from protobuf)
- **LNBolt**: C# BOLT protocol helpers (BigSize, TLV, onion routing, key derivation)
- **LNUnit.Tests**: Integration tests demonstrating the framework

## Development Commands

### Build
```bash
dotnet restore
dotnet build
```

### Format Check
```bash
dotnet tool install --global dotnet-format
dotnet format --verify-no-changes
```

### Run Tests
```bash
cd LNUnit.Tests
dotnet test --filter FullyQualifiedName~LNUnit.Test --verbosity normal
```

### Run Specific Test
```bash
cd LNUnit.Tests
dotnet test --filter "FullyQualifiedName~YourTestClassName.YourTestMethodName"
```

### Package NuGet
Packages are built automatically on build for projects with `GeneratePackageOnBuild` enabled.

## Architecture

### Core Components

**LNUnitBuilder** (`LNUnit/Setup/LNUnitBuilder.cs`)
- Central orchestration class for setting up test networks
- Manages Docker containers for Bitcoin and Lightning nodes
- Handles node initialization, peer connections, and channel setup
- Uses fluent builder pattern with extension methods
- Key methods:
  - `Build()`: Spins up containers and initializes network
  - `Destroy()`: Tears down test environment
  - Helper methods: `AddBitcoinCoreNode()`, `AddPolarLNDNode()`, `AddPolarEclairNode()`, etc.

**LNUnitNetworkDefinition** (`LNUnit/Setup/LNUnitNetworkDefinition.cs`)
- Defines the structure of test networks (nodes, channels, configuration)
- Serializable to JSON for saving/loading network configurations
- Contains nested classes: `BitcoinNode`, `LndNode`, `EclairNode`, `CLNNode`, `Channel`

**LNDNodePool** (`LNUnit.LND/LNDNodePool.cs`)
- Manages a pool of LND node connections
- Monitors node readiness with periodic health checks
- Provides rebalancing functionality between nodes
- Uses `PeriodicTimer` for polling node states
- Implements `IDisposable` for cleanup

**LNDNodeConnection** (`LNUnit.LND/LNDNodeConnection.cs`)
- Wrapper around LND gRPC clients
- Manages gRPC channel with macaroon authentication and TLS
- Exposes typed clients: `LightningClient`, `RouterClient`, `StateClient`, etc.
- Provides node state checking: `IsRpcReady`, `IsServerReady`

### HTLC & Channel Management

The framework supports advanced Lightning features:
- **Channel Acceptors**: `LNDChannelAcceptor` for programmatic channel acceptance
- **HTLC Interceptors**: `LNDSimpleHtlcInterceptorHandler` for intercepting and modifying HTLCs
- **Channel Events**: `LNDChannelEventsHandler` for monitoring channel state changes
- **Custom Messages**: `LNDCustomMessageHandler` for protocol extensions

### LNBolt Utilities

BOLT protocol implementation helpers in `LNBolt/`:
- **BigSize**: Native BOLT #1 BigSize integer parsing
- **TLV**: Type-Length-Value record decoding
- **OnionBlob**: Onion routing packet decoding (BOLT #4)
- **ECKeyPair**: Key pair generation and shared secret derivation
- **PayReq**: BOLT #11 invoice parsing

## Testing Patterns

Tests inherit from `AbcLightningAbstractTests` which:
- Sets up a test network with multiple Lightning nodes
- Provides fixtures for different database backends (SQLite, BoltDB, Postgres)
- Handles setup/teardown of Docker containers
- Injects logging via Serilog

Common test flow:
1. `OneTimeSetup`: Initialize `LNUnitBuilder` and build network
2. `PerTestSetUp`: Reset state, generate new blocks
3. Test execution: Use `Builder` to interact with nodes
4. `Dispose`: Cleanup containers and resources

Example node access:
```csharp
var alice = await Builder.GetNodeFromAlias("alice");
var invoice = await Builder.GeneratePaymentRequestFromAlias("bob", new Invoice { Value = 1000 });
var payment = await Builder.MakeLightningPaymentFromAlias("alice", new SendPaymentRequest { PaymentRequest = invoice.PaymentRequest });
```

## Docker Requirements

### macOS Networking
Docker containers need direct network access. Use either:
- **Docker Mac Net Connect**: `brew install chipmk/tap/docker-mac-net-connect && sudo brew services start chipmk/tap/docker-mac-net-connect`
- **OrbStack**: Works out of the box

### Container Images
Default images from Polar Lightning:
- Bitcoin: `polarlightning/bitcoind:29.0`
- LND: `polarlightning/lnd:0.17.4-beta`
- Eclair: `polarlightning/eclair:0.6.0`
- CLN: `polarlightning/clightning:0.10.0`

## gRPC Code Generation

LND gRPC clients in `LNUnit.LND/` are generated from protobuf files in `Grpc/`:
- Uses `Grpc.Tools` package for code generation
- Configured in `LNUnit.LND.csproj` with `<Protobuf Include=...>` directives
- Generated code outputs to `obj/` directories during build
- Covers all LND subsystems: main API, router, signer, invoices, wallet, etc.

## Project Dependencies

Key NuGet packages:
- **Grpc.Net.ClientFactory** (2.71.0): gRPC client infrastructure
- **Google.Protobuf** (3.33.1): Protocol buffer serialization
- **Docker.DotNet** (3.125.15): Docker API client
- **NBitcoin** (9.0.3): Bitcoin primitives and scripting
- **ServiceStack** (8.10.0): JSON serialization and utilities
- **NUnit** (3.14.0): Testing framework

## Target Framework

All projects target **.NET 10.0** (`net10.0`) with:
- `ImplicitUsings` enabled
- `Nullable` reference types enabled
- C# language version 12.0-14.0

## ConfigureAwait Pattern

The codebase consistently uses `ConfigureAwait(false)` on all `await` statements to avoid deadlocks and improve performance in library code.

## Async/Streaming Patterns

gRPC streaming calls use `AsyncServerStreamingCall`:
```csharp
var stream = client.StreamingMethod(request);
await foreach (var response in stream.ResponseStream.ReadAllAsync())
{
    // Process response
}
```

## Important Notes

- Always dispose `LNUnitBuilder` and `LNDNodeConnection` instances
- Test environments run on Bitcoin regtest network
- Channel policies (fees, HTLC limits) can be configured per channel
- Use `WaitUntilSyncedToChain()` before channel operations
- The framework handles macaroon authentication automatically via base64-encoded values extracted from Docker containers
