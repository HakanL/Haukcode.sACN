using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Haukcode.sACN.Model;
using Xunit;

namespace Haukcode.sACN.Test
{
    public class SACNTerminationTests
    {
        private static FramingOptions OptionsOf(SACNDataPacket packet)
        {
            var buffer = new byte[packet.Length];
            packet.WriteToBuffer(buffer);

            return FramingOptions.Parse(buffer[112]);
        }

        [Fact]
        public void Update_SetsPreviewAndForceSynchronization()
        {
            var packet = new SACNDataPacket(1, "Source", Guid.NewGuid(), 0, ReadOnlyMemory<byte>.Empty, 100);

            packet.Update(1, 1, new byte[1], 100, syncAddress: 7, preview: true);
            var options = OptionsOf(packet);
            Assert.True(options.PreviewData);
            Assert.True(options.ForceSynchronization);

            packet.Update(1, 2, new byte[1], 100, syncAddress: 7, forceSynchronization: false);
            options = OptionsOf(packet);
            Assert.False(options.PreviewData);
            Assert.False(options.ForceSynchronization);

            // Without a sync address the bit has no meaning and stays clear
            packet.Update(1, 3, new byte[1], 100, syncAddress: 0, forceSynchronization: true);
            Assert.False(OptionsOf(packet).ForceSynchronization);
        }

        [Fact]
        public async Task Terminate_SendsThreePackets_AndTheyAreDelivered()
        {
            var received = new ConcurrentQueue<(byte Sequence, bool Terminated)>();
            int port = Random.Shared.Next(40000, 60000);

            using var client = new SACNClient(
                Guid.NewGuid(),
                "Test",
                IPAddress.Loopback,
                channelWriter: p =>
                {
                    // The packet is a reused scratch instance, so copy what we need
                    if (p.Packet is SACNDataPacket data && data.UniverseId == 42)
                        received.Enqueue((data.SequenceId, data.DataFramingLayer.Options.StreamTerminated));

                    return Task.CompletedTask;
                },
                port: port);

            await client.SendDmxData(IPAddress.Loopback, 42, new byte[512]);
            await client.SendDmxData(IPAddress.Loopback, 42, new byte[512], terminate: true);

            for (int i = 0; i < 100 && received.Count < 4; i++)
                await Task.Delay(20);

            var packets = received.ToArray();
            Assert.Equal(4, packets.Length);
            Assert.False(packets[0].Terminated);
            Assert.All(packets[1..], p => Assert.True(p.Terminated));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, packets.Select(p => p.Sequence));
        }
    }
}
