using Haukcode.sACN;
using Haukcode.sACN.Model;
using System;
using System.Buffers;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Haukcode.sACN.ConsoleExample;

public class Program
{
    private static readonly Guid acnSourceId = new Guid("{B32625A6-C280-4389-BD25-E0D13F5B50E0}");
    private static readonly string acnSourceName = "DMXPlayer";

    private static MemoryPool<byte> memoryPool = MemoryPool<byte>.Shared;
    private static double last = 0;

    public static void Main(string[] args)
    {
        Listen();
    }

    static void Listen()
    {
        var channel = Channel.CreateUnbounded<ReceiveDataPacket>();

        var recvClient = new SACNClient(
            senderId: acnSourceId,
            senderName: acnSourceName,
            localAddress: IPAddress.Any,
            channelWriter: p => WritePacket(channel, p),
            channelWriterComplete: () => channel.Writer.Complete());

        var sendClient = new SACNClient(
            senderId: acnSourceId,
            senderName: acnSourceName,
            localAddress: Haukcode.Network.Utils.GetFirstBindAddress().IPAddress);

        recvClient.OnError.Subscribe(e =>
        {
            Console.WriteLine($"Error! {e.Message}");
        });

        var writerTask = Task.Factory.StartNew(async () =>
        {
            await WriteToWriterAsync(channel, CancellationToken.None);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        recvClient.JoinDMXUniverse(1);
        recvClient.JoinDMXUniverse(2);

        while (true)
        {
            sendClient.SendDmxData(null, 1, new byte[] { 1, 2, 3, 4, 5 });

            Thread.Sleep(500);
        }
    }

    private static async Task WritePacket(Channel<ReceiveDataPacket> channel, ReceiveDataPacket receiveData)
    {
        var dmxData = TransformPacket(receiveData);

        if (dmxData == null)
            return;

        await channel.Writer.WriteAsync(dmxData, CancellationToken.None);
    }

    private static ReceiveDataPacket TransformPacket(ReceiveDataPacket receiveData)
    {
        var framingLayer = receiveData.Packet.RootLayer?.FramingLayer;
        if (framingLayer == null)
            return null;

        switch (framingLayer)
        {
            case DataFramingLayer dataFramingLayer:
                var dmpLayer = dataFramingLayer.DMPLayer;

                if (dmpLayer == null || dmpLayer.Length < 1)
                    // Unknown/unsupported
                    return null;

                if (dmpLayer.StartCode != 0)
                    // We only support start code 0
                    return null;

                // Hack
                var newBuf = new byte[dmpLayer.Data.Length];
                dmpLayer.Data.CopyTo(newBuf);
                dmpLayer.Data = newBuf;

                return receiveData;

            case SyncFramingLayer syncFramingLayer:
                return receiveData;
        }

        return null;
    }

    private static async Task WriteToWriterAsync(Channel<ReceiveDataPacket> inputChannel, CancellationToken cancellationToken)
    {
        await foreach (var dmxData in inputChannel.Reader.ReadAllAsync(cancellationToken))
        {
            Listener_OnPacket(dmxData.TimestampMS, dmxData.TimestampMS - last, dmxData);
            last = dmxData.TimestampMS;
        }
    }

    private static void Listener_OnPacket(double timestampMS, double sinceLast, ReceiveDataPacket e)
    {
        var dataPacket = e.Packet.RootLayer.FramingLayer as DataFramingLayer;

        if (dataPacket == null)
            return;

        Console.Write($"+{sinceLast:N2}\t");
        Console.Write($"Packet from {dataPacket.SourceName}\tu{dataPacket.UniverseId}\ts{dataPacket.SequenceId}\t");
        Console.WriteLine($"Data {string.Join(",", dataPacket.DMPLayer.Data.ToArray().Take(16))}...");
    }
}
