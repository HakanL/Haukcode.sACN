# Haukcode.sACN [![NuGet Version](http://img.shields.io/nuget/v/Haukcode.sACN.svg?style=flat)](https://www.nuget.org/packages/Haukcode.sACN/)

A high-performance, cross-platform sACN (E1.31) library for .NET

## Table of Contents
- [What is sACN (E1.31)?](#what-is-sacn-e131)
- [Features](#features)
- [Installation](#installation)
- [Upgrading from 2.x](#upgrading-from-2x)
- [Quick Start](#quick-start)
- [Usage Examples](#usage-examples)
  - [Receiving DMX Data](#receiving-dmx-data)
  - [Sending DMX Data](#sending-dmx-data)
  - [Multicast vs Unicast](#multicast-vs-unicast)
  - [Sync Packets](#sync-packets)
  - [Universe Discovery](#universe-discovery)
- [What the Application Handles](#what-the-application-handles)
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
- **Priority**: each source sends a priority (0-200, default 100). A receiver with several sources on one universe follows the highest priority; how it merges sources with equal priority (often HTP, Highest Takes Precedence) is up to the receiver
- **Sync Packets**: Synchronization packets that ensure multiple universes update simultaneously for effects that span multiple fixtures

**Official Specification**: [ANSI E1.31-2018](https://tsp.esta.org/tsp/documents/docs/ANSI_E1-31-2018.pdf)

## Features

✅ **Full sACN/E1.31 Support**
- Send and receive DMX data over sACN
- Multicast and unicast transmission modes
- Sync packet support for synchronized universe updates
- Universe discovery (sending and receiving)
- Stream termination, preview data and priority fields

✅ **High Performance**
- Built on high-performance communication primitives
- No per-packet allocations on the send and receive paths
- Dedicated sender threads, optionally sharded across several sockets
- Packets parsed on the receive thread and handed straight to your callback

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
<PackageReference Include="Haukcode.sACN" Version="3.0.*" />
```

## Upgrading from 2.x

3.0 brings the library in line with ANSI E1.31-2018 and has breaking changes. The
[3.0 release notes](https://github.com/HakanL/Haukcode.sACN/releases) list them all; the
ones most likely to affect you:

- **Received packets are reused.** The packet handed to `channelWriter` is overwritten by the
  next datagram as soon as the writer returns. The 2.x README examples queued it as-is, which
  returns corrupted data; queue `p.Clone()` instead (see [Packet Lifetime](#packet-lifetime)).
- **Stream_Terminated packets are delivered.** Check `Options.StreamTerminated` before applying
  a packet's DMX data. `terminate: true` now sends the three packets E1.31 requires.
- **Arguments are validated.** Universes must be 1-63999, priority 0-200 and DMX data at most
  512 bytes, or `SendDmxData` and the join methods throw `ArgumentOutOfRangeException`.
- **Universe discovery works with other products.** 2.x sent a malformed packet to the wrong
  address. `SendUniverseDiscovery` no longer takes page numbers; it pages for you.
- **Unsigned types.** Wire constants and lengths that E1.31 defines as unsigned are `ushort` or
  `uint`, and `SACNPacket.Parse` returns null for datagrams that aren't E1.31.

## Quick Start

### Receiving DMX Data

```csharp
using Haukcode.sACN;
using Haukcode.sACN.Model;
using System;
using System.Net;
using System.Threading.Channels;

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
        if (dataLayer.Options.StreamTerminated)
        {
            // The source stopped sending this universe; ignore the data in this packet
            Console.WriteLine($"Universe {dataLayer.UniverseId}: {dataLayer.SourceName} terminated");
            continue;
        }

        Console.WriteLine($"Universe {dataLayer.UniverseId}: {dataLayer.DMPLayer.Data.Length} channels");
    }
}

async Task WritePacketAsync(Channel<ReceiveDataPacket> ch, ReceiveDataPacket pkt)
{
    // The packet is reused for the next datagram once this returns, so queue a copy
    await ch.Writer.WriteAsync(pkt.Clone());
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
            // Received packets are reused once the writer returns, so queue a copy
            channelWriter: async p => await channel.Writer.WriteAsync(p.Clone()),
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
        // Send the termination packets (three, as E1.31 requires)
        await client.SendDmxData(
            address: null,
            universeId: 1,
            dmxData: Array.Empty<byte>(),
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
- Uses multicast group addresses 239.255.0.1 - 239.255.249.255 (universes 1-63999)
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

Join a universe as a trigger (for example a sync address) rather than as DMX input. Both
join the universe's multicast group, so everything sent to it is delivered, data and sync
packets alike; the difference is the `SubscribeMode` on each received packet, so you can tell
why it arrived. A sync packet carries the subscription of its sync address.

```csharp
// Join universe 7000 as a trigger listener
client.JoinDMXUniverseForTrigger(7000);

// In the channel writer
if (p.Packet.FramingLayer is SyncFramingLayer sync && p.SubscribeMode.HasFlag(SubscribeModes.Trigger))
    OnSync(sync.SyncAddress);
```

### Universe Discovery

Sources announce the universes they send on 239.255.250.214 (universe 64214). E1.31 expects
an announcement every 10 seconds; the client sorts the list and splits it into pages of 512.

```csharp
// Announce (call every 10 seconds while sending)
await client.SendUniverseDiscovery(null, new ushort[] { 1, 2, 3 });

// Listen for other sources' announcements
client.JoinDiscoveryMulticastGroup();

// In the channel writer
if (p.Packet is SACNUniverseDiscoveryPacket discovery)
    Console.WriteLine($"{discovery.SourceName}: page {discovery.Page}/{discovery.LastPage}, universes {string.Join(",", discovery.Universes)}");
```

## What the Application Handles

The library sends and receives E1.31 packets: it builds them to the spec, and on receive it
discards packets the spec says to discard. The receiver and sender behavior E1.31 describes on
top of that is up to the application:

**Receiving**
- **Sequence numbers** (E1.31 section 6.7.2): drop a packet whose sequence number is behind
  the last one from the same source and universe: new minus last, as a signed byte, between
  -19 and 0.
- **Data loss** (6.7.1): treat a source as gone after 2.5 s without packets, or immediately on
  a packet with `Options.StreamTerminated` (ignore that packet's DMX data).
- **Merging**: pick the highest-priority source per universe, and merge equal priorities.
  Identify sources by CID (`packet.UUID`), not IP address.
- **Preview data**: ignore packets with `Options.PreviewData` for live output.
- **Synchronization**: hold data packets that have a sync address until the sync packet arrives.
- **Filtering**: the client delivers every packet that reaches it. Unicast data for universes
  you haven't joined arrives with `SubscribeMode.None`. On Windows the client also receives its
  own multicast; skip packets where `packet.UUID == client.SenderId`.

**Sending**
- **Keep-alive**: keep sending each universe while it's active. When the data stops changing,
  E1.31 allows slowing down but expects at least one packet a second.
- **Discovery**: call `SendUniverseDiscovery` every 10 seconds.
- **Termination**: `terminate: true` when you stop sending a universe.

## API Reference

### SACNClient Constructor

```csharp
public SACNClient(
    Guid senderId,                                    // CID: unique, stable identifier for this source
    string senderName,                                // Source name, at most 63 UTF-8 bytes (longer is cut)
    IPAddress localAddress,                           // Local network interface to bind to
    Func<ReceiveDataPacket, Task>? channelWriter = null,  // Called for each received packet (see Packet Lifetime)
    Action? channelWriterComplete = null,             // Called when receiving stops
    int port = 5568,                                  // sACN port (default 5568)
    int senderCount = 1)                              // Sender threads/sockets, sharded by universe
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
    bool important = false,  // Never dropped by the send queue (age cut-off or bound)
    bool terminate = false,  // Terminate the stream (sends three packets)
    bool preview = false,    // Preview_Data: for visualizers, not live output
    bool forceSynchronization = true)  // With a sync address: carry on unsynchronized if sync stops

// Send sync packet
Task SendSync(
    IPAddress? address,      // null for multicast, IP for unicast
    ushort syncAddress)      // Sync universe ID (1-63999)

// Announce the universes this source sends (every 10 seconds)
Task SendUniverseDiscovery(
    IPAddress? address,      // null for the discovery multicast group
    IEnumerable<ushort> universes,  // Sorted and split into pages of 512 for you
    bool important = true)
```

`SendDmxData` and `SendSync` may be called from several threads.

### Receiving Methods

```csharp
// Join universe to receive full DMX data
void JoinDMXUniverse(ushort universeId)

// Drop universe subscription
void DropDMXUniverse(ushort universeId)

// Drop all universe subscriptions
void DropAllInputUniverses()

// Join universe as a trigger (see Trigger Universes)
void JoinDMXUniverseForTrigger(ushort universeId)

// Drop trigger universe
void DropDMXUniverseForTrigger(ushort universeId)

// Drop all trigger subscriptions
void DropAllTriggerUniverses()

// Listen for universe discovery announcements
void JoinDiscoveryMulticastGroup()
void DropDiscoveryMulticastGroup()
```

Universes are 1-63999.

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

### Packet Lifetime

To avoid allocating per packet, the client parses every received data packet into the same
`ReceiveDataPacket`, and the DMX data is a slice of its single receive buffer. Both are
overwritten by the next datagram as soon as your `channelWriter` returns. Either process the
packet inside the writer, or keep a copy:

```csharp
// Zero-copy: handle it on the receive thread, before returning
channelWriter: p =>
{
    if (p.Packet.FramingLayer is DataFramingLayer data && !data.Options.StreamTerminated)
        ApplyDmx(data.UniverseId, data.DMPLayer.Data.Span);

    return Task.CompletedTask;
}

// Or queue an independent copy to process later
channelWriter: p => channel.Writer.WriteAsync(p.Clone()).AsTask()
```

Don't put the packet itself in a queue or keep a reference to it: every queued item would be
the same object, holding whatever arrived last.

### Stream Termination

Properly terminate streams when done. `terminate: true` sends the three packets with the
Stream_Terminated bit that E1.31 requires; stop sending to the universe afterwards.

```csharp
// Send the termination packets for a universe
await client.SendDmxData(
    address: null,
    universeId: 1,
    dmxData: Array.Empty<byte>(),
    terminate: true);
```

Received termination packets are delivered to the channel writer. Check
`DataFramingLayer.Options.StreamTerminated` and treat the source as gone for that universe;
E1.31 says to ignore the DMX data in these packets.

### Multiple Network Interfaces

List and select specific network interfaces:

```csharp
// Get the IPv4 interfaces (Ethernet and Wi-Fi)
var interfaces = Haukcode.Network.Utils.GetCommonInterfaces();

foreach (var nic in interfaces)
{
    Console.WriteLine($"{nic.AdapterName}: {nic.IPAddress}");
}

// Use specific interface
var selectedInterface = interfaces.First(a => a.AdapterName.Contains("Ethernet"));
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
- Universe N → 239.255.(N/256).(N%256), up to universe 63999 → 239.255.249.255
- Universe discovery → 239.255.250.214

Ensure your network switches support IGMP snooping for efficient multicast delivery.

### Performance Tuning

The library sets reasonable defaults, but you can monitor buffer usage:

```csharp
Console.WriteLine($"Receive buffer size: {client.ActualReceiveBufferSize} bytes");
```

For high-traffic scenarios:
- Use dedicated network interface for sACN
- Ensure switches have sufficient multicast capacity
- Raise `senderCount` if one sender thread can't keep up
- Monitor for packet loss using sequence numbers

## Performance Considerations

- **Memory**: No per-packet allocations; received packets are reused (see Packet Lifetime)
- **Threading**: Dedicated sender and receive threads; `channelWriter` runs on the receive thread, so keep it fast
- **Packet Rate**: E1.31 sets no maximum; DMX512 itself refreshes at up to about 44 Hz
- **Universes**: Can handle hundreds of universes simultaneously
- **Latency**: Minimal processing latency, suitable for real-time control

## Troubleshooting

### No packets received

1. **Check network interface**: Ensure you're binding to the correct interface
   ```csharp
   var interfaces = Haukcode.Network.Utils.GetCommonInterfaces();
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
