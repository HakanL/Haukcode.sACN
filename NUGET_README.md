# Haukcode.sACN

A high-performance, cross-platform sACN (E1.31) library for .NET - enabling DMX512 lighting control over IP networks.

## What is sACN?

sACN (Streaming ACN / ANSI E1.31) is a network protocol for transmitting DMX512 lighting control data over IP networks using UDP. It's the industry standard for modern entertainment and architectural lighting systems.

## Features

- ✅ Send and receive DMX data via sACN/E1.31
- ✅ Multicast and unicast support
- ✅ Sync packet support for frame-accurate multi-universe synchronization
- ✅ Universe discovery, stream termination and priority
- ✅ High-performance with buffer pooling and async I/O
- ✅ Cross-platform (.NET 8.0, 9.0, 10.0)
- ✅ Reactive extensions support

## Quick Start

### Receiving DMX Data

```csharp
using Haukcode.sACN;
using Haukcode.sACN.Model;
using System.Net;
using System.Threading.Channels;

var channel = Channel.CreateUnbounded<ReceiveDataPacket>();

var client = new SACNClient(
    senderId: Guid.NewGuid(),
    senderName: "MyReceiver",
    localAddress: IPAddress.Any,
    // Received packets are reused once the writer returns, so queue a copy
    channelWriter: async p => await channel.Writer.WriteAsync(p.Clone()),
    channelWriterComplete: () => channel.Writer.Complete());

// Join Universe 1
client.JoinDMXUniverse(1);

// Process packets
await foreach (var packet in channel.Reader.ReadAllAsync())
{
    // Sync and discovery packets arrive here too
    if (packet.Packet.FramingLayer is not DataFramingLayer data)
        continue;

    if (data.Options.StreamTerminated)
    {
        Console.WriteLine($"Universe {data.UniverseId}: {data.SourceName} stopped");
        continue;
    }

    Console.WriteLine($"Universe {data.UniverseId}: {data.DMPLayer.Data.Length} channels");
}
```

### Sending DMX Data

```csharp
using Haukcode.sACN;
using System.Net;

var client = new SACNClient(
    senderId: Guid.NewGuid(),
    senderName: "MyController",
    localAddress: Haukcode.Network.Utils.GetFirstBindAddress().IPAddress);

byte[] dmxData = new byte[512];
dmxData[0] = 255;  // Channel 1 at full intensity

// Send via multicast
await client.SendDmxData(null, universeId: 1, dmxData);

// Send via unicast to specific device
await client.SendDmxData(IPAddress.Parse("192.168.1.100"), universeId: 1, dmxData);
```

## Key Methods

**Sending:**
- `SendDmxData()` - Send DMX channel data
- `SendSync()` - Send synchronization packet
- `SendUniverseDiscovery()` - Announce the universes you send (every 10 s)

**Receiving:**
- `JoinDMXUniverse()` - Subscribe to a universe
- `DropDMXUniverse()` - Unsubscribe from a universe
- `JoinDMXUniverseForTrigger()` - Join a universe as a trigger (for sync addresses)
- `JoinDiscoveryMulticastGroup()` - Listen for universe discovery

Sequence checking, the 2.5 s data loss timeout, source merging and keep-alive are left to the
application; the GitHub README lists what E1.31 expects.

## Documentation

For comprehensive documentation, advanced features, API reference, and troubleshooting:

📖 **[Full Documentation on GitHub](https://github.com/HakanL/Haukcode.sACN#readme)**

## Resources

- [ANSI E1.31-2018 Specification](https://tsp.esta.org/tsp/documents/docs/ANSI_E1-31-2018.pdf)
- [GitHub Repository](https://github.com/HakanL/Haukcode.sACN)
- [NuGet Package](https://www.nuget.org/packages/Haukcode.sACN/)

## License

MIT License - see [LICENSE](https://github.com/HakanL/Haukcode.sACN/blob/master/LICENSE) for details.
