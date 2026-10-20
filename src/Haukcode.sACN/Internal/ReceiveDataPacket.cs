using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Haukcode.sACN.Model;

namespace Haukcode.sACN;

/// <summary>
/// A received packet, handed to the client's channel writer. For data packets this object, its
/// packet and the DMX data are reused for the next datagram as soon as the writer returns: use
/// them inside the writer, or keep a <see cref="Clone"/> instead.
/// </summary>
public class ReceiveDataPacket
{
    public double TimestampMS { get; set; }

    public IPEndPoint Source { get; set; } = null!;

    public IPEndPoint? Destination { get; set; }

    public SACNPacket Packet { get; set; } = null!;

    public SubscribeModes SubscribeMode { get; set; }

    /// <summary>
    /// An independent copy that stays valid after the channel writer returns, with its own copy
    /// of the DMX data. Sync and discovery packets are allocated per datagram, so they're shared.
    /// </summary>
    public ReceiveDataPacket Clone()
    {
        return new ReceiveDataPacket
        {
            TimestampMS = TimestampMS,
            Source = Source,
            Destination = Destination,
            Packet = Packet is SACNDataPacket dataPacket ? dataPacket.Clone() : Packet,
            SubscribeMode = SubscribeMode
        };
    }
}
