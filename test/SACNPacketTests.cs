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
            var sourcePacket = new SACNPacket(universeID, "SourceName", new Guid(), 1, new byte[512], 1);
            byte[] packetData = sourcePacket.ToArray();

            var parsedPacket = SACNPacket.Parse(packetData);
            Assert.Equal(universeID, parsedPacket.UniverseId);
        }

        [Theory]
        [InlineData(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11)]
        public void EncodeParse_GuidIsCorrect(int a, short b, short c, byte d, byte e, byte f, byte g, byte h, byte i, byte j, byte k)
        {
            Guid expected = new Guid(a, b, c, d, e, f, g, h, i, j, k);
            var sourcePacket = new SACNPacket(1, "SourceName", expected, 1, new byte[512], 1);
            byte[] packetData = sourcePacket.ToArray();

            var parsedPacket = SACNPacket.Parse(packetData);
            Guid actual = parsedPacket.RootLayer.UUID;
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(1)]
        public void EncodeParse_SequenceIDIsCorrect(byte sequenceID)
        {
            var sourcePacket = new SACNPacket(1, "SourceName", new Guid(), sequenceID, new byte[512], 1);
            byte[] packetData = sourcePacket.ToArray();

            var parsedPacket = SACNPacket.Parse(packetData);
            Assert.Equal(sequenceID, parsedPacket.SequenceId);
        }

        [Theory]
        [InlineData(15)]
        public void EncodeParse_PriorityIsCorrect(byte priority)
        {
            var sourcePacket = new SACNPacket(1, "SourceName", new Guid(), 1, new byte[512], priority);
            byte[] packetData = sourcePacket.ToArray();

            var parsedPacket = SACNPacket.Parse(packetData);
            Assert.Equal(priority, parsedPacket.RootLayer.FramingLayer.Priority);
        }

        [Theory]
        [InlineData("Source Name")]
        public void EncodeParse_SourceNameIsCorrect(string sourceName)
        {
            var sourcePacket = new SACNPacket(1, sourceName, new Guid(), 1, new byte[512], 1);
            byte[] packetData = sourcePacket.ToArray();

            var parsedPacket = SACNPacket.Parse(packetData);
            Assert.Equal(sourceName, parsedPacket.SourceName);
        }
    }
}
