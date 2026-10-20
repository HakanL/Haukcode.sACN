using System;
using System.Buffers.Binary;
using System.IO;
using Haukcode.sACN.Model;
using Xunit;

namespace Haukcode.sACN.Test
{
    /// <summary>
    /// Receive-side validation: packets E1.31 says to discard are rejected, malformed ones throw
    /// InvalidDataException, and datagrams that aren't E1.31 at all parse to null.
    /// </summary>
    public class SACNPacketValidationTests
    {
        private static byte[] DataPacket(int slots = 512)
        {
            var packet = new SACNDataPacket(1, "Source", Guid.NewGuid(), 1, new byte[slots], 100);
            var buffer = new byte[packet.Length];
            packet.WriteToBuffer(buffer);

            return buffer;
        }

        private static byte[] SyncPacket()
        {
            var packet = new SACNPacket(RootLayer.CreateRootLayerSync(Guid.NewGuid(), 1, 10));
            var buffer = new byte[packet.Length];
            packet.WriteToBuffer(buffer);

            return buffer;
        }

        [Fact]
        public void ValidDataPacket_Parses()
        {
            Assert.IsType<SACNDataPacket>(SACNPacket.Parse(DataPacket()));
            Assert.IsType<SACNDataPacket>(SACNPacket.Parse(DataPacket(slots: 0)));
        }

        [Fact]
        public void TrailingBytesAfterThePacket_AreIgnored()
        {
            var buffer = new byte[1000];
            DataPacket().CopyTo(buffer, 0);

            Assert.IsType<SACNDataPacket>(SACNPacket.Parse(buffer));
        }

        [Theory]
        [InlineData(43, 0x07)]   // framing vector
        [InlineData(117, 0x03)]  // DMP vector
        [InlineData(118, 0xA2)]  // address type & data type
        [InlineData(120, 0x01)]  // first property address
        [InlineData(122, 0x02)]  // address increment
        [InlineData(16, 0x60)]   // root flags
        [InlineData(38, 0x60)]   // framing flags
        [InlineData(115, 0x60)]  // DMP flags
        public void InvalidDataPacketField_Throws(int offset, byte value)
        {
            var buffer = DataPacket();
            buffer[offset] = (byte)((offset is 16 or 38 or 115) ? (buffer[offset] & 0x0F) | value : value);

            Assert.Throws<InvalidDataException>(() => SACNPacket.Parse(buffer));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(514)]
        [InlineData(0x8000)]
        [InlineData(512)]
        public void InvalidPropertyValueCount_Throws(int count)
        {
            var buffer = DataPacket();
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(123), (ushort)count);

            Assert.Throws<InvalidDataException>(() => SACNPacket.Parse(buffer));
        }

        [Theory]
        [InlineData(20)]
        [InlineData(37)]
        [InlineData(100)]
        [InlineData(300)]
        [InlineData(637)]
        public void TruncatedDataPacket_Throws(int length)
        {
            Assert.Throws<InvalidDataException>(() => SACNPacket.Parse(DataPacket().AsMemory(0, length)));
        }

        [Fact]
        public void MismatchedLayerLengths_Throw()
        {
            var buffer = DataPacket();
            // Framing layer one byte shorter than the root layer says
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(38), (ushort)(0x7000 | (638 - 38 - 1)));

            Assert.Throws<InvalidDataException>(() => SACNPacket.Parse(buffer));
        }

        [Fact]
        public void SyncPacketWithWrongLength_Throws()
        {
            var buffer = new byte[60];
            SyncPacket().CopyTo(buffer, 0);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(16), (ushort)(0x7000 | (50 - 16)));
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(38), (ushort)(0x7000 | (50 - 38)));

            Assert.Throws<InvalidDataException>(() => SACNPacket.Parse(buffer));
        }

        [Fact]
        public void SyncPacket_Parses()
        {
            var packet = SACNPacket.Parse(SyncPacket());

            var framing = Assert.IsType<SyncFramingLayer>(packet!.FramingLayer);
            Assert.Equal(10, framing.SyncAddress);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        [InlineData(15)]
        [InlineData(100)]
        public void NotAnAcnPacket_ReturnsNull(int length)
        {
            var buffer = new byte[length];
            new Random(length).NextBytes(buffer);

            Assert.Null(SACNPacket.Parse(buffer));
        }

        [Fact]
        public void WrongPacketIdentifier_ReturnsNull()
        {
            var buffer = DataPacket();
            buffer[8] = (byte)'X';

            Assert.Null(SACNPacket.Parse(buffer));
        }

        [Fact]
        public void UnknownRootVector_ReturnsNull()
        {
            var buffer = DataPacket();
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(18), 0x00000005);

            Assert.Null(SACNPacket.Parse(buffer));
        }

        [Fact]
        public void UnknownExtendedFramingVector_ReturnsNull()
        {
            var buffer = SyncPacket();
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(40), 0x00000003);

            Assert.Null(SACNPacket.Parse(buffer));
        }

        [Fact]
        public void ChangingThePublicPacketIdentifier_DoesNotAffectTheLibrary()
        {
            byte original = RootLayer.PACKET_IDENTIFIER[0];
            try
            {
                RootLayer.PACKET_IDENTIFIER[0] = (byte)'X';

                var buffer = DataPacket();
                Assert.Equal((byte)'A', buffer[4]);
                Assert.IsType<SACNDataPacket>(SACNPacket.Parse(buffer));
            }
            finally
            {
                RootLayer.PACKET_IDENTIFIER[0] = original;
            }
        }
    }
}
