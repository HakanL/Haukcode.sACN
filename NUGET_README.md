# Haukcode.sACN

A high-performance, cross-platform sACN (E1.31) library for .NET - enabling DMX512 lighting control over IP networks.

## What is sACN?

sACN (Streaming ACN / ANSI E1.31) is a network protocol for transmitting DMX512 lighting control data over IP networks using UDP. It's the industry standard for modern entertainment and architectural lighting systems.

## Features

- ✅ Send and receive DMX data via sACN/E1.31
- ✅ Multicast and unicast support
- ✅ Sync packet support for frame-accurate multi-universe synchronization
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
    channelWriter: async p => await channel.Writer.WriteAsync(p),
    channelWriterComplete: () => channel.Writer.Complete());

// Join Universe 1
client.JoinDMXUniverse(1);

// Process packets
await foreach (var packet in channel.Reader.ReadAllAsync())
{
    var data = packet.Packet.RootLayer.FramingLayer as DataFramingLayer;
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

**Receiving:**
- `JoinDMXUniverse()` - Subscribe to a universe
- `DropDMXUniverse()` - Unsubscribe from a universe
- `JoinDMXUniverseForTrigger()` - Listen for sync packets only

## Documentation

For comprehensive documentation, advanced features, API reference, and troubleshooting:

📖 **[Full Documentation on GitHub](https://github.com/HakanL/Haukcode.sACN#readme)**

## Resources

- [ANSI E1.31-2018 Specification](https://tsp.esta.org/tsp/documents/docs/ANSI_E1-31-2018.pdf)
- [GitHub Repository](https://github.com/HakanL/Haukcode.sACN)
- [NuGet Package](https://www.nuget.org/packages/Haukcode.sACN/)

## License

MIT License - see [LICENSE](https://github.com/HakanL/Haukcode.sACN/blob/master/LICENSE) for details.
