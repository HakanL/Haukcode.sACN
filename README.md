# Haukcode.sACN [![NuGet Version](http://img.shields.io/nuget/v/Haukcode.sACN.svg?style=flat)](https://www.nuget.org/packages/Haukcode.sACN/)

A high-performance, cross-platform sACN (E1.31) library for .NET

## Table of Contents
- [What is sACN (E1.31)?](#what-is-sacn-e131)
- [Features](#features)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Usage Examples](#usage-examples)
  - [Receiving DMX Data](#receiving-dmx-data)
  - [Sending DMX Data](#sending-dmx-data)
  - [Multicast vs Unicast](#multicast-vs-unicast)
  - [Sync Packets](#sync-packets)
- [API Reference](#api-reference)
- [Advanced Features](#advanced-features)
- [Network Configuration](#network-configuration)
- [Performance Considerations](#performance-considerations)
- [Troubleshooting](#troubleshooting)
- [Credits](#credits)
- [License](#license)

## What is sACN (E1.31)?

sACN (Streaming ACN) is a network protocol standardized as ANSI E1.31, designed for efficiently transmitting DMX512 lighting control data over IP networks using multicast or unicast UDP packets. It's widely used in entertainment lighting, architectural lighting, and stage production.

**Key Concepts:**
- **DMX512**: A standard protocol for controlling lighting fixtures, allowing up to 512 channels of 8-bit data per universe
- **Universe**: A logical grouping of up to 512 DMX channels (e.g., Universe 1 might control stage lights, Universe 2 might control house lights)
- **Multicast**: Efficient one-to-many transmission where packets are sent to a multicast group address
- **Unicast**: One-to-one transmission where packets are sent directly to a specific IP address
- **Priority**: sACN supports source priority (0-200), allowing multiple controllers to manage the same universe with Highest Takes Precedence (HTP) logic
- **Sync Packets**: Synchronization packets that ensure multiple universes update simultaneously for effects that span multiple fixtures

**Official Specification**: [ANSI E1.31-2018](https://tsp.esta.org/tsp/documents/docs/ANSI_E1-31-2018.pdf)

## Features

✅ **Full sACN/E1.31 Support**
- Send and receive DMX data over sACN
- Multicast and unicast transmission modes
- Sync packet support for synchronized universe updates
- Priority-based source arbitration

✅ **High Performance**
- Built on high-performance communication primitives
- Efficient memory management with buffer pooling
- Asynchronous operations with System.Threading.Channels
- Optimized packet parsing and serialization

✅ **Cross-Platform**
- Supports .NET 8.0, 9.0, and 10.0
- Works on Windows, Linux, and macOS
- Platform-specific optimizations

✅ **Easy to Use**
- Simple, intuitive API
- Reactive extensions support (System.Reactive)
- Comprehensive error handling
- Flexible subscription model for universe listening

## Installation

Install via NuGet Package Manager:

```bash
dotnet add package Haukcode.sACN
```

Or via Package Manager Console:

```powershell
Install-Package Haukcode.sACN
```

Or add directly to your `.csproj` file:

```xml
<PackageReference Include="Haukcode.sACN" Version="2.0.*" />
```

## Quick Start

### Receiving DMX Data

```csharp
using Haukcode.sACN;
using Haukcode.sACN.Model;
using System.Net;

// Create a unique sender ID and name for this client
var senderId = Guid.NewGuid();
var senderName = "MyApp";

// Channel for receiving packets
var channel = Channel.CreateUnbounded<ReceiveDataPacket>();

// Create receive client
var client = new SACNClient(
    senderId: senderId,
    senderName: senderName,
    localAddress: IPAddress.Any,
    channelWriter: p => WritePacketAsync(channel, p),
    channelWriterComplete: () => channel.Writer.Complete());

// Join Universe 1 to start receiving data
client.JoinDMXUniverse(1);

// Process incoming packets
await foreach (var packet in channel.Reader.ReadAllAsync())
{
    var dataLayer = packet.Packet.RootLayer.FramingLayer as DataFramingLayer;
    if (dataLayer != null)
    {
        Console.WriteLine($"Universe {dataLayer.UniverseId}: {dataLayer.DMPLayer.Data.Length} channels");
    }
}

async Task WritePacketAsync(Channel<ReceiveDataPacket> ch, ReceiveDataPacket pkt)
{
    await ch.Writer.WriteAsync(pkt);
}
```

### Sending DMX Data

```csharp
using Haukcode.sACN;
using System.Net;

var senderId = Guid.NewGuid();
var senderName = "MyDMXController";

// Get local network interface
var localAddress = Haukcode.Network.Utils.GetFirstBindAddress().IPAddress;

// Create send client
var client = new SACNClient(
    senderId: senderId,
    senderName: senderName,
    localAddress: localAddress);

// Prepare DMX data (512 channels, values 0-255)
byte[] dmxData = new byte[512];
dmxData[0] = 255;  // Channel 1 at full
dmxData[1] = 128;  // Channel 2 at 50%

// Send to Universe 1 (multicast)
await client.SendDmxData(
    address: null,        // null = multicast
    universeId: 1,
    dmxData: dmxData,
    priority: 100);

// Send to specific device (unicast)
await client.SendDmxData(
    address: IPAddress.Parse("192.168.1.100"),
    universeId: 1,
    dmxData: dmxData,
    priority: 100);
```

## Usage Examples

### Receiving DMX Data

Here's a complete example of receiving and processing DMX data from multiple universes:

```csharp
using Haukcode.sACN;
using Haukcode.sACN.Model;
using System.Net;
using System.Threading.Channels;

public class DMXReceiver
{
    private SACNClient client;
    private Channel<ReceiveDataPacket> channel;

    public async Task StartAsync()
    {
        channel = Channel.CreateUnbounded<ReceiveDataPacket>();
        
        client = new SACNClient(
            senderId: Guid.NewGuid(),
            senderName: "DMX Receiver",
            localAddress: IPAddress.Any,
            channelWriter: async p => await channel.Writer.WriteAsync(p),
            channelWriterComplete: () => channel.Writer.Complete());

        // Subscribe to error events
        client.OnError.Subscribe(error =>
        {
            Console.WriteLine($"Error: {error.Message}");
        });

        // Join multiple universes
        client.JoinDMXUniverse(1);
        client.JoinDMXUniverse(2);
        client.JoinDMXUniverse(3);

        // Process packets
        await ProcessPacketsAsync();
    }

    private async Task ProcessPacketsAsync()
    {
        await foreach (var packet in channel.Reader.ReadAllAsync())
        {
            var dataLayer = packet.Packet.RootLayer.FramingLayer as DataFramingLayer;
            if (dataLayer != null)
            {
                var dmpLayer = dataLayer.DMPLayer;
                
                // Only process start code 0 (standard DMX)
                if (dmpLayer.StartCode == 0)
                {
                    Console.WriteLine($"Source: {dataLayer.SourceName}");
                    Console.WriteLine($"Universe: {dataLayer.UniverseId}");
                    Console.WriteLine($"Sequence: {dataLayer.SequenceId}");
                    Console.WriteLine($"Priority: {dataLayer.Priority}");
                    Console.WriteLine($"Channels: {dmpLayer.Data.Length}");
                    
                    // Access DMX channel values
                    var channelValues = dmpLayer.Data.ToArray();
                    Console.WriteLine($"Channel 1: {channelValues[0]}");
                }
            }
        }
    }

    public void Stop()
    {
        // Clean up
        client.DropAllInputUniverses();
        client.Dispose();
    }
}
```

### Sending DMX Data

Complete example showing how to send DMX data with various options:

```csharp
using Haukcode.sACN;
using System.Net;

public class DMXSender
{
    private SACNClient client;
    private byte[] dmxBuffer = new byte[512];

    public void Initialize()
    {
        var localIP = Haukcode.Network.Utils.GetFirstBindAddress().IPAddress;
        
        client = new SACNClient(
            senderId: Guid.NewGuid(),
            senderName: "My Lighting Controller",
            localAddress: localIP);
    }

    public async Task SendLightingDataAsync()
    {
        // Set some channel values
        dmxBuffer[0] = 255;   // Channel 1 - Dimmer at full
        dmxBuffer[1] = 200;   // Channel 2 - Red at 78%
        dmxBuffer[2] = 150;   // Channel 3 - Green at 59%
        dmxBuffer[3] = 100;   // Channel 4 - Blue at 39%

        // Send via multicast (standard sACN)
        await client.SendDmxData(
            address: null,
            universeId: 1,
            dmxData: dmxBuffer,
            priority: 100);
    }

    public async Task SendToSpecificDevice()
    {
        // Send via unicast to a specific device
        await client.SendDmxData(
            address: IPAddress.Parse("192.168.1.50"),
            universeId: 1,
            dmxData: dmxBuffer,
            priority: 100);
    }

    public async Task TerminateStream()
    {
        // Send termination packet
        await client.SendDmxData(
            address: null,
            universeId: 1,
            dmxData: new byte[0],
            priority: 100,
            terminate: true);
    }

    public void Dispose()
    {
        client?.Dispose();
    }
}
```

### Multicast vs Unicast

**Multicast** (pass `null` as address):
- Efficient for sending to multiple receivers
- Uses multicast group addresses (239.255.0.1 - 239.255.63.255)
- Receivers join multicast group for specific universe
- Standard sACN operation mode

```csharp
// Multicast to all listeners on Universe 1
await client.SendDmxData(null, 1, dmxData);
```

**Unicast** (pass specific IP address):
- Direct transmission to a single device
- Useful when multicast is blocked or unavailable
- Lower network overhead for single receiver

```csharp
// Unicast to specific device
await client.SendDmxData(IPAddress.Parse("192.168.1.100"), 1, dmxData);
```

### Sync Packets

Synchronization packets ensure multiple universes update simultaneously, essential for effects spanning multiple fixtures:

```csharp
// Send data to multiple universes with sync
ushort syncUniverse = 7000;  // Sync universe ID

// Send data with sync flag
await client.SendDmxData(null, 1, dmxData1, syncAddress: syncUniverse);
await client.SendDmxData(null, 2, dmxData2, syncAddress: syncUniverse);
await client.SendDmxData(null, 3, dmxData3, syncAddress: syncUniverse);

// Trigger synchronized update
await client.SendSync(null, syncUniverse);
```

**When to use sync packets:**
- Effects spanning multiple universes need to update at exactly the same time
- Avoiding visible tearing or phase issues in multi-universe setups
- Professional installations requiring frame-accurate synchronization

### Trigger Universes

Listen for sync packets on specific universes without receiving the full DMX data:

```csharp
// Join universe as trigger listener only
client.JoinDMXUniverseForTrigger(7000);

// This will receive sync packets but not full DMX data
// Useful for timing/synchronization without processing full data streams
```

## API Reference

### SACNClient Constructor

```csharp
public SACNClient(
    Guid senderId,                                    // Unique identifier for this source
    string senderName,                                // Human-readable source name (max 64 chars)
    IPAddress localAddress,                           // Local network interface to bind to
    Func<ReceiveDataPacket, Task>? channelWriter,    // Optional callback for received packets
    Action? channelWriterComplete,                    // Optional callback when receiving completes
    int port = 5568)                                  // sACN port (default 5568)
```

### Sending Methods

```csharp
// Send DMX data
Task SendDmxData(
    IPAddress? address,      // null for multicast, IP for unicast
    ushort universeId,       // Universe ID (1-63999)
    ReadOnlyMemory<byte> dmxData,  // Up to 512 bytes of DMX data
    byte priority = 100,     // Priority (0-200, default 100)
    ushort syncAddress = 0,  // Sync universe (0 = no sync)
    byte startCode = 0,      // Start code (0 = standard DMX)
    bool important = false,  // Priority queue flag
    bool terminate = false)  // Stream termination flag

// Send sync packet
Task SendSync(
    IPAddress? address,      // null for multicast, IP for unicast
    ushort syncAddress)      // Sync universe ID
```

### Receiving Methods

```csharp
// Join universe to receive full DMX data
void JoinDMXUniverse(ushort universeId)

// Drop universe subscription
void DropDMXUniverse(ushort universeId)

// Drop all universe subscriptions
void DropAllInputUniverses()

// Join universe for sync/trigger packets only
void JoinDMXUniverseForTrigger(ushort universeId)

// Drop trigger universe
void DropDMXUniverseForTrigger(ushort universeId)

// Drop all trigger subscriptions
void DropAllTriggerUniverses()
```

### Properties

```csharp
Guid SenderId { get; }           // This client's sender ID
string SenderName { get; }       // This client's sender name
IPEndPoint LocalEndPoint { get; }  // Local endpoint
int? ActualReceiveBufferSize { get; }  // Actual socket buffer size
IObservable<Exception> OnError { get; }  // Error notifications
```

## Advanced Features

### Error Handling

Subscribe to error events using reactive extensions:

```csharp
client.OnError.Subscribe(error =>
{
    Console.WriteLine($"sACN Error: {error.Message}");
    // Handle error appropriately
});
```

### Buffer Management

The library uses efficient buffer pooling to minimize allocations. When receiving data, the DMX data references memory from a shared pool. If you need to keep the data beyond the callback scope, copy it:

```csharp
var dataLayer = packet.Packet.RootLayer.FramingLayer as DataFramingLayer;
if (dataLayer != null)
{
    // Copy data if needed beyond this scope
    byte[] dmxCopy = dataLayer.DMPLayer.Data.ToArray();
    
    // Store or process dmxCopy later
}
```

### Stream Termination

Properly terminate streams when done:

```csharp
// Send termination packet for a universe
await client.SendDmxData(
    address: null,
    universeId: 1,
    dmxData: Array.Empty<byte>(),
    terminate: true);
```

### Multiple Network Interfaces

List and select specific network interfaces:

```csharp
// Get all available network interfaces
var adapters = Haukcode.Network.Utils.GetAllBindAddresses();

foreach (var adapter in adapters)
{
    Console.WriteLine($"{adapter.Name}: {adapter.IPAddress}");
}

// Use specific interface
var selectedInterface = adapters.First(a => a.Name.Contains("Ethernet"));
var client = new SACNClient(
    senderId: Guid.NewGuid(),
    senderName: "MyApp",
    localAddress: selectedInterface.IPAddress);
```

## Network Configuration

### Firewall Settings

Ensure UDP port 5568 is open for sACN communication:

**Windows:**
```powershell
New-NetFirewallRule -DisplayName "sACN" -Direction Inbound -Protocol UDP -LocalPort 5568 -Action Allow
```

**Linux (ufw):**
```bash
sudo ufw allow 5568/udp
```

### Multicast Configuration

sACN uses multicast addresses in the range `239.255.0.0/16`:
- Universe 1 → 239.255.0.1
- Universe 2 → 239.255.0.2
- Universe N → 239.255.(N/256).(N%256)

Ensure your network switches support IGMP snooping for efficient multicast delivery.

### Performance Tuning

The library sets reasonable defaults, but you can monitor buffer usage:

```csharp
Console.WriteLine($"Receive buffer size: {client.ActualReceiveBufferSize} bytes");
```

For high-traffic scenarios:
- Use dedicated network interface for sACN
- Enable Jumbo Frames if supported (MTU > 1500)
- Ensure switches have sufficient multicast capacity
- Monitor for packet loss using sequence numbers

## Performance Considerations

- **Memory**: Uses buffer pooling to minimize GC pressure
- **Threading**: Asynchronous I/O for non-blocking operations
- **Packet Rate**: Supports high packet rates (up to 40 packets/second per universe per E1.31 spec)
- **Universes**: Can handle hundreds of universes simultaneously
- **Latency**: Minimal processing latency, suitable for real-time control

## Troubleshooting

### No packets received

1. **Check network interface**: Ensure you're binding to the correct interface
   ```csharp
   var addresses = Haukcode.Network.Utils.GetAllBindAddresses();
   // Verify the address you're using
   ```

2. **Check universe subscription**: Verify you've joined the universe
   ```csharp
   client.JoinDMXUniverse(1);
   ```

3. **Firewall**: Ensure UDP port 5568 is allowed

4. **Multicast**: Verify multicast is enabled on network switches

### Packet loss

1. **Buffer size**: Check if receive buffer is sufficient
   ```csharp
   Console.WriteLine($"Buffer: {client.ActualReceiveBufferSize}");
   ```

2. **Processing speed**: Ensure packet processing is fast enough

3. **Network capacity**: Check for network congestion

### Platform-specific issues

**Linux**: Ensure proper permissions for multicast:
```bash
sudo sysctl -w net.ipv4.igmp_max_memberships=100
```

**Windows**: Verify Windows Firewall allows multicast traffic

## Credits

- Original fork from [kadmium-sacn-core](https://github.com/iKadmium/kadmium-sacn-core) by Jesse Higginson
- Maintained by Hakan Lindestaf
- Built with [Haukcode.Network](https://github.com/HakanL/Haukcode.Network) and [Haukcode.HighPerfComm](https://github.com/HakanL/Haukcode.HighPerfComm)

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

**Specification Reference**: [ANSI E1.31-2018 sACN Protocol](https://tsp.esta.org/tsp/documents/docs/ANSI_E1-31-2018.pdf)
