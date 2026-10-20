using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Haukcode.sACN.Model;
using Xunit;

namespace Haukcode.sACN.Test
{
    public class SACNConcurrentSendTests
    {
        [Fact]
        public async Task ConcurrentSenders_NeverMixUniverses()
        {
            const int Threads = 4;
            const int SendsPerThread = 5_000;

            int received = 0;
            int mismatched = 0;
            var lastSequence = new int[Threads + 1];
            int outOfOrder = 0;

            using var client = new SACNClient(
                Guid.NewGuid(),
                "Test",
                IPAddress.Loopback,
                channelWriter: p =>
                {
                    if (p.Packet is SACNDataPacket packet && packet.UniverseId is >= 1 and <= Threads)
                    {
                        // Universe u is always sent with u * 10 slots of value u at priority 100 + u
                        int u = packet.UniverseId;
                        var data = packet.DataFramingLayer.DMPLayer.Data.Span;
                        if (data.Length != u * 10 || packet.DataFramingLayer.Priority != 100 + u || data.IndexOfAnyExcept((byte)u) != -1)
                            Interlocked.Increment(ref mismatched);

                        // Sequence numbers per universe only move forward (UDP may drop some)
                        int diff = (sbyte)(packet.SequenceId - lastSequence[u]);
                        if (lastSequence[u] != 0 && diff <= 0)
                            Interlocked.Increment(ref outOfOrder);
                        lastSequence[u] = packet.SequenceId == 0 ? 256 : packet.SequenceId;

                        Interlocked.Increment(ref received);
                    }

                    return Task.CompletedTask;
                },
                port: TestPorts.GetFreeUdpPort());

            var senders = Enumerable.Range(1, Threads).Select(u => Task.Run(async () =>
            {
                var data = Enumerable.Repeat((byte)u, u * 10).ToArray();
                for (int i = 0; i < SendsPerThread; i++)
                {
                    await client.SendDmxData(IPAddress.Loopback, (ushort)u, data, priority: (byte)(100 + u), important: true);
                    if (i % 100 == 0)
                        await Task.Delay(1);
                }
            })).ToArray();

            await Task.WhenAll(senders);

            for (int i = 0; i < 100 && Volatile.Read(ref received) < Threads * SendsPerThread; i++)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            Assert.True(received > Threads * SendsPerThread / 2, $"Only {received} packets received");
            Assert.Equal(0, mismatched);
            Assert.Equal(0, outOfOrder);
        }
    }
}
