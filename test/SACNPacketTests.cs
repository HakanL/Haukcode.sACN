using System;
using Haukcode.sACN.Model;
using Xunit;

namespace Haukcode.sACN.Test
{
    public class SACNPacketTests
    {
        [Theory]
        [InlineData(1)]
        public void EncodeParse_UniverseIDIsCorrect(ushort universeID)
        {
            var sourcePacket = new SACNDataPacket(universeID, "SourceName", new Guid(), 1, new byte[512], 1);
            var packetData = new Memory<byte>(new byte[1024]);
            sourcePacket.WriteToBuffer(packetData);

            var parsedPacket = SACNPacket.Parse(packetData) as SACNDataPacket;
            Assert.Equal(universeID, parsedPacket.UniverseId);
        }

        [Theory]
        [InlineData(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11)]
        public void EncodeParse_GuidIsCorrect(int a, short b, short c, byte d, byte e, byte f, byte g, byte h, byte i, byte j, byte k)
        {
            Guid expected = new Guid(a, b, c, d, e, f, g, h, i, j, k);
            var sourcePacket = new SACNDataPacket(1, "SourceName", expected, 1, new byte[512], 1);
            var packetData = new Memory<byte>(new byte[1024]);
            sourcePacket.WriteToBuffer(packetData);

            var parsedPacket = SACNPacket.Parse(packetData);
            Guid actual = parsedPacket.RootLayer.UUID;
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(1)]
        public void EncodeParse_SequenceIDIsCorrect(byte sequenceID)
        {
            var sourcePacket = new SACNDataPacket(1, "SourceName", new Guid(), sequenceID, new byte[512], 1);
            var packetData = new Memory<byte>(new byte[1024]);
            sourcePacket.WriteToBuffer(packetData);

            var parsedPacket = SACNPacket.Parse(packetData);
            Assert.Equal(sequenceID, parsedPacket.SequenceId);
        }

        [Theory]
        [InlineData(15)]
        public void EncodeParse_PriorityIsCorrect(byte priority)
        {
            var sourcePacket = new SACNDataPacket(1, "SourceName", new Guid(), 1, new byte[512], priority);
            var packetData = new Memory<byte>(new byte[1024]);
            sourcePacket.WriteToBuffer(packetData);

            var parsedPacket = SACNPacket.Parse(packetData) as SACNDataPacket;
            Assert.Equal(priority, parsedPacket.DataFramingLayer.Priority);
        }

        [Theory]
        [InlineData("Source Name")]
        public void EncodeParse_SourceNameIsCorrect(string sourceName)
        {
            var sourcePacket = new SACNDataPacket(1, sourceName, new Guid(), 1, new byte[512], 1);
            var packetData = new Memory<byte>(new byte[1024]);
            sourcePacket.WriteToBuffer(packetData);

            var parsedPacket = SACNPacket.Parse(packetData) as SACNDataPacket;
            Assert.Equal(sourceName, parsedPacket.SourceName);
        }

        // Serializing a reused scratch packet reconfigured with Update() must produce byte-for-byte
        // the same output as constructing a fresh packet and applying the terminate/sync options the
        // way SendDmxData does — for several frames in a row, so no state leaks between reuses.
        [Fact]
        public void Update_ReusedPacket_SerializesIdenticalToFresh()
        {
            var id = new Guid(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);

            static byte[] Ramp(int len, int seed)
            {
                var b = new byte[len];
                for (int i = 0; i < len; i++)
                    b[i] = (byte)(i + seed);
                return b;
            }

            static byte[] Fresh(ushort uni, byte seq, byte[] data, byte pri, ushort sync, byte startCode, bool terminate, Guid id)
            {
                var p = new SACNDataPacket(uni, "SourceName", id, seq, data, pri, sync, startCode);
                if (terminate)
                    p.DataFramingLayer.Options.StreamTerminated = true;
                if (sync != 0)
                    p.DataFramingLayer.Options.ForceSynchronization = true;

                var buf = new byte[1024];
                int len = p.WriteToBuffer(buf);
                return buf[..len];
            }

            var reused = new SACNDataPacket(1, "SourceName", id, 0, ReadOnlyMemory<byte>.Empty, 100);

            byte[] Reused(ushort uni, byte seq, byte[] data, byte pri, ushort sync, byte startCode, bool terminate)
            {
                reused.Update(uni, seq, data, pri, sync, startCode, terminate);
                var buf = new byte[1024];
                int len = reused.WriteToBuffer(buf);
                return buf[..len];
            }

            // Frame 1: full universe, no sync/terminate
            Assert.Equal(Fresh(10, 5, Ramp(512, 0), 100, 0, 0, false, id), Reused(10, 5, Ramp(512, 0), 100, 0, 0, false));
            // Frame 2: different size/priority + sync -> ForceSynchronization set
            Assert.Equal(Fresh(20, 6, Ramp(256, 7), 150, 42, 0, false, id), Reused(20, 6, Ramp(256, 7), 150, 42, 0, false));
            // Frame 3: terminate flag
            Assert.Equal(Fresh(30, 7, Ramp(512, 1), 100, 0, 0, true, id), Reused(30, 7, Ramp(512, 1), 100, 0, 0, true));
            // Frame 4: back to plain -> confirms sync/terminate options were cleared on reuse
            Assert.Equal(Fresh(40, 8, Ramp(512, 2), 100, 0, 0, false, id), Reused(40, 8, Ramp(512, 2), 100, 0, 0, false));
        }
    }
}
