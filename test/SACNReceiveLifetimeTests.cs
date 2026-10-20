using System;
using System.Linq;
using System.Net;
using System.Threading.Channels;
using System.Threading.Tasks;
using Haukcode.sACN.Model;
using Xunit;

namespace Haukcode.sACN.Test
{
    public class SACNReceiveLifetimeTests
    {
        [Fact]
        public void DataPacketClone_CopiesEveryField()
        {
            var cid = Guid.NewGuid();
            var data = new byte[] { 1, 2, 3 };
            var packet = new SACNDataPacket(42, "Source", cid, 7, data, 150, syncAddress: 9, startCode: 0xDD);
            packet.DataFramingLayer.Options.PreviewData = true;
            packet.DataFramingLayer.Options.StreamTerminated = true;

            var copy = packet.Clone();
            data[0] = 99;

            Assert.Equal(42, copy.UniverseId);
            Assert.Equal("Source", copy.SourceName);
            Assert.Equal(cid, copy.UUID);
            Assert.Equal(7, copy.SequenceId);
            Assert.Equal(150, copy.DataFramingLayer.Priority);
            Assert.Equal(9, copy.DataFramingLayer.SyncAddress);
            Assert.Equal(0xDD, copy.DataFramingLayer.DMPLayer.StartCode);
            Assert.True(copy.DataFramingLayer.Options.PreviewData);
            Assert.True(copy.DataFramingLayer.Options.StreamTerminated);
            Assert.Equal(new byte[] { 1, 2, 3 }, copy.DMXData);
        }

        [Fact]
        public async Task QueuedClones_KeepTheirOwnData()
        {
            var channel = Channel.CreateUnbounded<ReceiveDataPacket>();

            using var client = new SACNClient(
                Guid.NewGuid(),
                "Test",
                IPAddress.Loopback,
                channelWriter: async p => await channel.Writer.WriteAsync(p.Clone()),
                port: Random.Shared.Next(40000, 60000));

            for (ushort universe = 1; universe <= 5; universe++)
                await client.SendDmxData(IPAddress.Loopback, universe, new[] { (byte)(universe * 10) });

            var received = new ReceiveDataPacket[5];
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            for (int i = 0; i < received.Length; i++)
                received[i] = await channel.Reader.ReadAsync(timeout.Token);

            Assert.Equal(5, received.Distinct().Count());
            Assert.Equal(new ushort[] { 1, 2, 3, 4, 5 }, received.Select(p => ((SACNDataPacket)p.Packet).UniverseId));
            Assert.Equal(new byte[] { 10, 20, 30, 40, 50 }, received.Select(p => ((SACNDataPacket)p.Packet).DMXData[0]));
        }
    }
}
