# LNUnit.LND - LLM Documentation

## Overview

LNUnit.LND provides .NET classes for connecting to and managing Lightning Network Daemon (LND) nodes via gRPC. The library simplifies LND integration by handling authentication, connection management, and common operations through a clean, type-safe API.

**Key Classes:**
- `LNDNodeConnection` - Single LND node connection with gRPC clients
- `LNDNodePool` - Manages a pool of LND nodes with health monitoring and load balancing
- `LNDSettings` - Configuration for connecting to an LND node
- `LNDNodePoolConfig` - Configuration for node pool setup

---

## LNDNodeConnection

### Purpose
`LNDNodeConnection` represents a single connection to an LND node, providing access to all LND gRPC services through strongly-typed clients.

### Constructor

```csharp
public LNDNodeConnection(LNDSettings settings, ILogger<LNDNodeConnection>? logger = null)
```

**Parameters:**
- `settings`: LNDSettings object containing connection details (TLS cert, macaroon, gRPC endpoint)
- `logger`: Optional ILogger for diagnostic logging

**Behavior:**
- Automatically establishes connection on construction
- Validates TLS certificate and macaroon are provided (throws InvalidOperationException if missing)
- Retrieves node identity and metadata on successful connection
- Logs connection details if logger is provided

### Core Properties

#### Connection Details
```csharp
public LNDSettings Settings { get; internal set; }
public string Host { get; internal set; }
```
- `Settings`: Original connection configuration
- `Host`: The gRPC endpoint URL (e.g., "https://localhost:10009")

#### Node Identity
```csharp
public string LocalNodePubKey { get; internal set; }
public byte[] LocalNodePubKeyBytes { get; }  // Computed from LocalNodePubKey
public string LocalAlias { get; internal set; }
public string ClearnetConnectString { get; internal set; }
public string OnionConnectString { get; internal set; }
```
- `LocalNodePubKey`: Node's public key as hex string
- `LocalNodePubKeyBytes`: Node's public key as byte array (convenience property)
- `LocalAlias`: Human-readable node alias
- `ClearnetConnectString`: Clearnet connection URI (pubkey@ip:port)
- `OnionConnectString`: Tor onion connection URI (pubkey@onion:port)

#### Node State
```csharp
public bool IsRpcReady { get; }
public bool IsServerReady { get; }
```
- `IsRpcReady`: True when node is in RpcActive or ServerActive state
- `IsServerReady`: True only when node is in ServerActive state (fully operational)

### gRPC Clients

All LND gRPC service clients are exposed as public properties:

```csharp
public Lightning.LightningClient LightningClient { get; internal set; }
public Router.RouterClient RouterClient { get; internal set; }
public Signer.SignerClient SignClient { get; internal set; }
public State.StateClient StateClient { get; internal set; }
public ChainNotifier.ChainNotifierClient ChainNotifierClient { get; internal set; }
public Dev.DevClient DevClient { get; internal set; }
public Invoices.InvoicesClient InvoiceClient { get; internal set; }
public Peers.PeersClient PeersClient { get; internal set; }
public WalletKit.WalletKitClient WalletKitClient { get; internal set; }
```

**Usage Pattern:**
```csharp
var connection = new LNDNodeConnection(settings);
var info = connection.LightningClient.GetInfo(new GetInfoRequest());
var channels = await connection.LightningClient.ListChannelsAsync(new ListChannelsRequest());
```

### Key Methods

#### GetStateSafe
```csharp
public WalletState GetStateSafe(double timeOutSeconds = 3)
```
Safely queries the node's wallet state with timeout protection.

**Parameters:**
- `timeOutSeconds`: Timeout in seconds (default: 3)

**Returns:** WalletState enum value (NonExisting if query fails)

**Behavior:**
- Catches and suppresses RpcException and general exceptions
- Returns WalletState.NonExisting on any error
- Use this method when you need fault-tolerant state checking

#### KeysendPayment
```csharp
public async Task<Payment?> KeysendPayment(
    string dest,
    long amtSat,
    long feeLimitSat = 10,
    string? message = null,
    int timeoutSeconds = 60,
    Dictionary<ulong, byte[]>? keySendPairs = null)
```
Sends a spontaneous payment (keysend) to a node without requiring an invoice.

**Parameters:**
- `dest`: Destination node public key (hex string)
- `amtSat`: Amount in satoshis
- `feeLimitSat`: Maximum fee limit in satoshis (default: 10)
- `message`: Optional message to include in custom records
- `timeoutSeconds`: Payment timeout (default: 60)
- `keySendPairs`: Optional custom TLV records to include

**Returns:** Payment object with status and details, or null if payment fails

**Implementation Details:**
- Generates random preimage and computes hash
- Includes keysend TLV record (5482373484) with preimage
- Optionally includes message TLV record (34349334)
- Uses RouterClient.SendPaymentV2 for streaming response
- Returns final payment state after stream completes

**Example:**
```csharp
var payment = await connection.KeysendPayment(
    dest: "03...",
    amtSat: 1000,
    feeLimitSat: 50,
    message: "Coffee payment"
);
if (payment?.Status == Payment.Types.PaymentStatus.Succeeded) {
    Console.WriteLine("Payment successful!");
}
```

#### Clone
```csharp
public LNDNodeConnection Clone()
```
Creates a new independent connection using the same settings.

**Returns:** New LNDNodeConnection instance

**Use Cases:**
- Creating separate connections for concurrent operations
- Using in disposable contexts (e.g., monitoring handlers)
- Avoiding connection sharing issues

#### Stop & Dispose
```csharp
public Task Stop()
public void Dispose()
```
- `Stop()`: Disposes the gRPC channel (returns completed task)
- `Dispose()`: Implements IDisposable, calls Stop internally

**Important:** Always dispose connections when done to free gRPC resources.

### Connection Setup Details

The `CreateGrpcConnection` method handles:
- Setting GRPC_SSL_CIPHER_SUITES environment variable to "HIGH+ECDSA"
- Loading X509 certificate from base64-encoded TLS cert
- Converting macaroon from base64 to hex for authentication
- Creating CallCredentials with macaroon metadata
- Configuring large message sizes (128MB max receive/send)
- Custom certificate validation (accepts all certificates)

### Common Usage Patterns

#### Basic Connection
```csharp
var settings = new LNDSettings {
    GrpcEndpoint = "https://localhost:10009",
    TlsCertBase64 = "LS0tLS1...",
    MacaroonBase64 = "AgEDbG5k..."
};

using var connection = new LNDNodeConnection(settings, logger);

// Wait for ready state
while (!connection.IsServerReady) {
    await Task.Delay(500);
}

// Use clients
var info = connection.LightningClient.GetInfo(new GetInfoRequest());
Console.WriteLine($"Connected to {info.Alias}");
```

#### Payment Operations
```csharp
// Regular invoice payment
var invoice = await connection.LightningClient.AddInvoiceAsync(new Invoice {
    Value = 1000,
    Memo = "Test payment"
});

// Pay invoice
var payment = new SendPaymentRequest {
    PaymentRequest = invoice.PaymentRequest,
    TimeoutSeconds = 60,
    FeeLimitSat = 10
};
var response = connection.RouterClient.SendPaymentV2(payment);
await foreach (var update in response.ResponseStream.ReadAllAsync()) {
    if (update.Status == Payment.Types.PaymentStatus.Succeeded) {
        Console.WriteLine("Payment succeeded!");
    }
}
```

#### Channel Management
```csharp
// List active channels
var channels = await connection.LightningClient.ListChannelsAsync(
    new ListChannelsRequest { ActiveOnly = true }
);

foreach (var channel in channels.Channels) {
    Console.WriteLine($"Channel {channel.ChanId}: " +
        $"Local: {channel.LocalBalance}, Remote: {channel.RemoteBalance}");
}
```

---

## LNDNodePool

### Purpose
`LNDNodePool` manages multiple LND node connections with automatic health monitoring, ready-state tracking, and pool-based operations like rebalancing.

### Constructors

#### Dependency Injection Constructor (Recommended)
```csharp
public LNDNodePool(
    IOptionsSnapshot<LNDNodePoolConfig> lndNodePoolConfig,
    ILogger<LNDNodePool> logger,
    IServiceProvider serviceProvider)
```

#### Direct Config Constructor
```csharp
public LNDNodePool(
    LNDNodePoolConfig lndNodePoolConfig,
    ILogger<LNDNodePool> logger,
    IServiceProvider serviceProvider)
```

#### Legacy Constructor (Obsolete)
```csharp
[Obsolete]
public LNDNodePool(
    List<LNDSettings> nodeSettings,
    int updateReadyStatesPeriod = 5,
    bool quickStartupMode = true)
```

### Core Properties

```csharp
public readonly List<LNDNodeConnection> ReadyNodes;
public int TotalNodes { get; internal set; }
public bool AllReady => ReadyNodes.Count == TotalNodes;
public Func<BalanceTask, Task>? SaveRebalanceAction { get; set; }
```

- `ReadyNodes`: List of nodes currently in ServerActive state (ready for operations)
- `TotalNodes`: Total number of nodes in pool (including not-yet-connected)
- `AllReady`: True when all nodes are connected and ready
- `SaveRebalanceAction`: Optional callback for persisting rebalance operations

### Configuration

The pool accepts `LNDNodePoolConfig` with:
- `ConnectTo`: List of LNDSettings to establish new connections
- `Nodes`: List of pre-existing LNDNodeConnection objects
- `UpdateReadyStatesPeriod`: Polling interval in seconds (default: 5, range: 1-60)
- `QuickStartupMode`: If true, polls at 100ms intervals until 10s timeout or all nodes ready, then switches to UpdateReadyStatesPeriod

### Automatic Health Monitoring

The pool runs a background task that:
1. Attempts to initialize nodes from `ConnectTo` list (retries on failure)
2. Checks each node's ServerActive state periodically
3. Adds ready nodes to `ReadyNodes` list
4. Removes nodes from `ReadyNodes` if they become unavailable
5. Uses QuickStartupMode for fast initial connection (100ms polling)
6. Switches to normal polling rate after startup period

**Timing:**
- QuickStartupMode: 100ms polling for first 10 seconds
- Normal mode: Uses `UpdateReadyStatesPeriod` (configurable, default 5s)

### Key Methods

#### GetLNDNodeConnection
```csharp
public LNDNodeConnection GetLNDNodeConnection()
public LNDNodeConnection? GetLNDNodeConnection(string pubkey)
```

**Overload 1 (no parameters):**
- Returns the first ready node from `ReadyNodes`
- Throws if no ready nodes available
- Use for simple load distribution (returns first available)

**Overload 2 (with pubkey):**
- Returns specific node by public key, or null if not found
- Searches all nodes, not just ready ones

**Example:**
```csharp
// Get any ready node
var node = pool.GetLNDNodeConnection();

// Get specific node
var aliceNode = pool.GetLNDNodeConnection("03abc123...");
if (aliceNode != null) {
    // Use alice node
}
```

#### AddNode
```csharp
public void AddNode(LNDSettings nodeSettings)
```
Adds a new node to the pool by adding settings to the uninitialized list.

**Behavior:**
- Node will be initialized on next health monitoring cycle
- Does not immediately create connection
- Pool will attempt connection in background

#### RemoveNode
```csharp
public void RemoveNode(LNDNodeConnection node)
```
Removes a node from the pool.

**Behavior:**
- Removes from both internal nodes list and ReadyNodes list
- Does NOT dispose the connection (caller's responsibility)

#### RebalanceNodePool
```csharp
public async Task<PoolRebalanceStats> RebalanceNodePool(int deltaThreshold = 100_000)
```
Rebalances channels between pool members to achieve 50/50 local/remote balance.

**Parameters:**
- `deltaThreshold`: Minimum satoshi imbalance required to trigger rebalance (default: 100,000)

**Returns:** PoolRebalanceStats with:
- `TotalRebalanceCount`: Number of successful rebalances
- `TotalAmount`: Total satoshis moved
- `Tasks`: List of BalanceTask objects with details

**Algorithm:**
1. Identifies all channels between pool members
2. Calculates imbalance (difference from 50/50 split)
3. Creates rebalance tasks for channels exceeding deltaThreshold
4. Executes rebalances via invoice/payment method (zero fees between direct peers)
5. Records payment hashes and optionally persists via SaveRebalanceAction

**Example:**
```csharp
// Set up persistence callback (optional)
pool.SaveRebalanceAction = async (task) => {
    await database.SaveAsync(task);
};

// Run rebalance with 50k sat minimum threshold
var stats = await pool.RebalanceNodePool(deltaThreshold: 50_000);
Console.WriteLine($"Rebalanced {stats.TotalRebalanceCount} channels, " +
    $"moved {stats.TotalAmount} sats");
```

#### InvoicePayRebalance (Static)
```csharp
public static async Task<string?> InvoicePayRebalance(
    LNDNodeConnection src,
    LNDNodeConnection dest,
    long valueInSatoshis,
    ILogger? logger = null,
    ulong channelId = 0)
```
Rebalances a specific channel by generating invoice on dest and paying from src.

**Parameters:**
- `src`: Source node (funds move from local to remote)
- `dest`: Destination node (funds move from remote to local)
- `valueInSatoshis`: Amount to rebalance
- `logger`: Optional logger
- `channelId`: Optional specific outgoing channel ID

**Returns:** Payment hash as hex string on success, null on failure

**Process:**
1. Creates 60-second invoice on destination node
2. Pays invoice from source node with 20-second timeout
3. Optionally routes through specific channel if channelId provided

#### GetInteralNodeEvenBalaceTasks (Static)
```csharp
public static async Task<List<BalanceTask>> GetInteralNodeEvenBalaceTasks(
    LNDNodePool pool,
    int deltaThreshold = 100_000)
```
Analyzes pool and generates list of rebalance tasks needed for 50/50 channel splits.

**Returns:** List of BalanceTask objects describing required rebalances

**Algorithm:**
1. Identifies all active channels between pool members
2. Filters to channels with imbalance > deltaThreshold
3. Determines direction (which side has more balance)
4. Calculates amount needed to reach 50/50 split
5. Adjusts amounts to respect channel constraints (min/max HTLC sizes)
6. Deduplicates (each channel appears once)

### Common Usage Patterns

#### Setup with Dependency Injection
```csharp
// In Startup.cs or Program.cs
services.Configure<LNDNodePoolConfig>(config => {
    config.UpdateReadyStatesPeriod = 5;
    config.ConnectTo.Add(new LNDSettings {
        GrpcEndpoint = "https://alice:10009",
        TlsCertBase64 = "...",
        MacaroonBase64 = "..."
    });
    config.ConnectTo.Add(new LNDSettings {
        GrpcEndpoint = "https://bob:10009",
        TlsCertBase64 = "...",
        MacaroonBase64 = "..."
    });
});

services.AddSingleton<LNDNodePool>();

// In your service
public class MyService {
    private readonly LNDNodePool _pool;

    public MyService(LNDNodePool pool) {
        _pool = pool;
    }

    public async Task DoWork() {
        // Wait for nodes to be ready
        while (!_pool.AllReady) {
            await Task.Delay(500);
        }

        // Use any ready node
        var node = _pool.GetLNDNodeConnection();
        var info = node.LightningClient.GetInfo(new GetInfoRequest());
    }
}
```

#### Builder Pattern Configuration
```csharp
var config = new LNDNodePoolConfig()
    .AddConnectionSettings(new LNDSettings {
        GrpcEndpoint = "https://alice:10009",
        TlsCertBase64 = "...",
        MacaroonBase64 = "..."
    })
    .AddConnectionSettings(new LNDSettings {
        GrpcEndpoint = "https://bob:10009",
        TlsCertBase64 = "...",
        MacaroonBase64 = "..."
    })
    .UpdateReadyStatesPeriod(10);

var pool = new LNDNodePool(config, logger, serviceProvider);
```

#### Monitoring and Operations
```csharp
// Check pool status
Console.WriteLine($"Ready: {pool.ReadyNodes.Count}/{pool.TotalNodes}");

// Wait for specific node
var alice = pool.GetLNDNodeConnection("03abc...");
while (alice == null) {
    await Task.Delay(500);
    alice = pool.GetLNDNodeConnection("03abc...");
}

// Perform rebalancing
var stats = await pool.RebalanceNodePool();
foreach (var task in stats.Tasks) {
    Console.WriteLine($"Rebalanced channel {task.ChannelPoint}: " +
        $"{task.Amount} sats from {task.SrcPk.Substring(0,6)}... " +
        $"to {task.DestPk.Substring(0,6)}...");
}
```

#### Graceful Shutdown
```csharp
// Dispose pool (stops monitoring and disposes all connections)
pool.Dispose();
```

---

## LNDSettings

### Purpose
Configuration class for LND node connection parameters.

### Properties
```csharp
public class LNDSettings
{
    public string? GrpcEndpoint { get; set; }      // e.g., "https://localhost:10009"
    public string? TlsCertBase64 { get; set; }     // Base64-encoded TLS certificate
    public string? MacaroonBase64 { get; set; }    // Base64-encoded macaroon
}
```

### Obtaining Connection Parameters

**From LND files:**
```bash
# TLS Certificate (typically at ~/.lnd/tls.cert)
base64 -w 0 ~/.lnd/tls.cert

# Admin Macaroon (typically at ~/.lnd/data/chain/bitcoin/mainnet/admin.macaroon)
base64 -w 0 ~/.lnd/data/chain/bitcoin/mainnet/admin.macaroon
```

**Example:**
```csharp
var settings = new LNDSettings {
    GrpcEndpoint = "https://192.168.1.100:10009",
    TlsCertBase64 = "LS0tLS1CRUdJTi...",
    MacaroonBase64 = "AgEDbG5kAvg..."
};
```

### Extension Method
```csharp
public static LNDNodeConnection GetClient(this LNDSettings settings)
```
Convenience method to create connection directly from settings.

**Usage:**
```csharp
var connection = settings.GetClient();
```

---

## LNDNodePoolConfig

### Purpose
Configuration class for LNDNodePool setup.

### Properties
```csharp
public class LNDNodePoolConfig
{
    public List<LNDSettings> ConnectTo { get; }                  // Nodes to connect to
    public List<LNDNodeConnection> Nodes { get; }                // Pre-existing connections
    [Range(1, 60)]
    public int UpdateReadyStatesPeriod { get; set; } = 5;        // Seconds between health checks
    public bool QuickStartupMode { get; } = true;                // Fast polling on startup
}
```

### Builder Extensions
```csharp
public static class LNDNodePoolConfigBuilder
{
    public static LNDNodePoolConfig AddNode(
        this LNDNodePoolConfig config,
        LNDNodeConnection connection)

    public static LNDNodePoolConfig AddConnectionSettings(
        this LNDNodePoolConfig config,
        LNDSettings settings)

    public static LNDNodePoolConfig UpdateReadyStatesPeriod(
        this LNDNodePoolConfig config,
        int period)
}
```

**Usage:**
```csharp
var config = new LNDNodePoolConfig()
    .AddConnectionSettings(aliceSettings)
    .AddConnectionSettings(bobSettings)
    .UpdateReadyStatesPeriod(10);
```

---

## Supporting Types

### PoolRebalanceStats
```csharp
public class PoolRebalanceStats
{
    public int TotalRebalanceCount { get; set; }
    public ulong TotalAmount { get; set; }
    public List<BalanceTask> Tasks { get; set; }
}
```

### BalanceTask
```csharp
public record BalanceTask
{
    public required string ChannelPoint { get; set; }
    public ulong ChanId { get; set; }
    public required string SrcPk { get; set; }
    public required string DestPk { get; set; }
    public long Amount { get; set; }
    public byte[]? PaymentHash { get; set; }
}
```

---

## Error Handling

### Connection Errors
- Constructor throws `InvalidOperationException` if TlsCertBase64 or MacaroonBase64 is null
- Network errors during GetInfo call will propagate as RpcException

### Pool Initialization Errors
- Failed node connections are logged and retried on next health check cycle
- Individual node failures don't prevent pool from operating with remaining nodes
- Use `AllReady` property to determine if all nodes are operational

### Payment Errors
- KeysendPayment returns null on failure (catches exceptions internally)
- InvoicePayRebalance returns null on failure (catches exceptions internally)
- For detailed error information, use try/catch around RouterClient.SendPaymentV2

---

## Best Practices

### Connection Management
1. Always dispose LNDNodeConnection when done (use `using` statement)
2. Use Clone() when you need concurrent independent connections
3. Check IsServerReady before performing critical operations
4. Use GetStateSafe() for non-critical state checks

### Pool Management
1. Wait for AllReady before performing pool-wide operations
2. Use dependency injection for pool lifecycle management
3. Implement SaveRebalanceAction for audit trails
4. Monitor ReadyNodes.Count for health metrics
5. Dispose pool on application shutdown

### Payment Operations
1. Always set appropriate fee limits to prevent overpaying
2. Use timeouts to prevent indefinite waits
3. Check payment status after completion
4. For keysend, ensure recipient accepts spontaneous payments

### Performance
1. QuickStartupMode reduces initial connection time
2. Reuse connections instead of creating new ones
3. Use appropriate UpdateReadyStatesPeriod (longer = less overhead)
4. Consider connection pooling for high-throughput scenarios

---

## Common Pitfalls

1. **Not waiting for IsServerReady**: Operations may fail if node isn't fully synced
2. **Forgetting to dispose**: Leads to gRPC channel leaks
3. **Hardcoding node references**: Use GetLNDNodeConnection(pubkey) for specific nodes
4. **Ignoring fee limits**: Can result in expensive routing failures
5. **Not handling null returns**: KeysendPayment and InvoicePayRebalance return null on failure

---

## Thread Safety

- LNDNodeConnection: Individual gRPC clients are thread-safe, but sharing a single connection for high-concurrency operations may have performance implications
- LNDNodePool: ReadyNodes list is modified by background task; clone the list before iteration in concurrent scenarios
- For high-concurrency scenarios, consider using Clone() to create per-thread connections

---

## Dependencies

- Grpc.Net.Client - gRPC communication
- Lnrpc - LND protobuf definitions
- Microsoft.Extensions.Logging - Logging support
- Microsoft.Extensions.DependencyInjection - DI support
- Microsoft.Extensions.Options - Configuration support

---

## Version Compatibility

This documentation is for LNUnit.LND version 3.x, compatible with:
- .NET 8.0+
- .NET 9.0
- .NET 10.0
- LND v0.15.0+

---

## Examples Collection

### Example 1: Simple Single-Node Connection
```csharp
var settings = new LNDSettings {
    GrpcEndpoint = "https://localhost:10009",
    TlsCertBase64 = File.ReadAllText("tls.cert.base64"),
    MacaroonBase64 = File.ReadAllText("admin.macaroon.base64")
};

using var connection = new LNDNodeConnection(settings);
var info = connection.LightningClient.GetInfo(new GetInfoRequest());
Console.WriteLine($"Node: {info.Alias} ({info.IdentityPubkey})");
Console.WriteLine($"Channels: {info.NumActiveChannels}");
Console.WriteLine($"Peers: {info.NumPeers}");
```

### Example 2: Pool with Multiple Nodes
```csharp
var pool = new LNDNodePool(new List<LNDSettings> {
    new() { GrpcEndpoint = "https://alice:10009", ... },
    new() { GrpcEndpoint = "https://bob:10009", ... },
    new() { GrpcEndpoint = "https://carol:10009", ... }
}, updateReadyStatesPeriod: 5);

// Wait for all nodes
while (!pool.AllReady) {
    Console.WriteLine($"Waiting for nodes: {pool.ReadyNodes.Count}/{pool.TotalNodes}");
    await Task.Delay(1000);
}

// Rebalance channels
var stats = await pool.RebalanceNodePool(deltaThreshold: 100_000);
Console.WriteLine($"Rebalanced {stats.TotalRebalanceCount} channels");
```

### Example 3: Keysend Payment
```csharp
var alice = new LNDNodeConnection(aliceSettings);
var bobPubkey = "03...";  // Bob's node pubkey

var payment = await alice.KeysendPayment(
    dest: bobPubkey,
    amtSat: 1000,
    feeLimitSat: 50,
    message: "Thanks for the coffee!",
    timeoutSeconds: 60
);

if (payment?.Status == Payment.Types.PaymentStatus.Succeeded) {
    Console.WriteLine($"Payment successful! Hash: {payment.PaymentHash}");
} else {
    Console.WriteLine($"Payment failed: {payment?.FailureReason}");
}
```

### Example 4: Channel Rebalancing
```csharp
var alice = pool.GetLNDNodeConnection("03abc...");
var bob = pool.GetLNDNodeConnection("03def...");

// Rebalance 500k sats from Alice to Bob
var paymentHash = await LNDNodePool.InvoicePayRebalance(
    src: alice,
    dest: bob,
    valueInSatoshis: 500_000,
    logger: logger
);

if (paymentHash != null) {
    Console.WriteLine($"Rebalance successful: {paymentHash}");
}
```

### Example 5: Monitoring Node Health
```csharp
var connection = new LNDNodeConnection(settings);

// Continuous health monitoring
var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
while (await timer.WaitForNextTickAsync()) {
    var state = connection.GetStateSafe();
    Console.WriteLine($"Node state: {state}");

    if (connection.IsServerReady) {
        var channels = await connection.LightningClient.ListChannelsAsync(
            new ListChannelsRequest { ActiveOnly = true }
        );
        Console.WriteLine($"Active channels: {channels.Channels.Count}");
    }
}
```

---

## Testing Utilities

The library includes test utilities in LNUnit.Tests for integration testing:

- `LNUnitBuilder`: Docker-based test network setup
- `AbcLightningAbstractTests`: Base class for LND integration tests
- Support for custom LND images and configurations
- PostgreSQL and BoltDB backend testing

See test files for comprehensive usage examples.
