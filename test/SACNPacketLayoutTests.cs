using System;
using System.Buffers.Binary;
using Haukcode.sACN.Model;
using Xunit;

namespace Haukcode.sACN.Test
{
    /// <summary>
    /// Byte offsets checked against ANSI E1.31-2018 Table 4-1 (Data), 4-2 (Sync) and 4-3 (Universe Discovery).
    /// </summary>
    public class SACNPacketLayoutTests
    {
        private static readonly Guid Cid = new Guid("0123456789abcdef0123456789abcdef");

        private static ushort U16(byte[] b, int offset) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(offset));

        private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(offset));

        private static byte[] Write(SACNPacket packet)
        {
            var buffer = new byte[2000];
            int length = packet.WriteToBuffer(buffer);
            Assert.Equal(packet.Length, length);

            return buffer[..length];
        }

        [Fact]
        public void DataPacket_MatchesSpecOffsets()
        {
            var data = new byte[512];
            data[0] = 0x11;
            data[511] = 0x22;
            var packet = new SACNDataPacket(0xFA00, "Src", Cid, 0x9C, data, 200, syncAddress: 0x1234, startCode: 0xDD);

            var b = Write(packet);

            Assert.Equal(638, b.Length);

            // Root layer
            Assert.Equal(0x0010, U16(b, 0));
            Assert.Equal(0x0000, U16(b, 2));
            Assert.Equal("ASC-E1.17\0\0\0"u8.ToArray(), b[4..16]);
            Assert.Equal(0x7000 | (638 - 16), U16(b, 16));
            Assert.Equal(0x00000004u, U32(b, 18));
            Assert.Equal(Cid.ToByteArray(bigEndian: true), b[22..38]);

            // Framing layer
            Assert.Equal(0x7000 | (638 - 38), U16(b, 38));
            Assert.Equal(0x00000002u, U32(b, 40));
            Assert.Equal((byte)'S', b[44]);
            Assert.Equal(0, b[47]);
            Assert.Equal(200, b[108]);
            Assert.Equal(0x1234, U16(b, 109));
            Assert.Equal(0x9C, b[111]);
            Assert.Equal(0xFA00, U16(b, 113));

            // DMP layer
            Assert.Equal(0x7000 | (638 - 115), U16(b, 115));
            Assert.Equal(0x02, b[117]);
            Assert.Equal(0xA1, b[118]);
            Assert.Equal(0x0000, U16(b, 119));
            Assert.Equal(0x0001, U16(b, 121));
            Assert.Equal(513, U16(b, 123));
            Assert.Equal(0xDD, b[125]);
            Assert.Equal(0x11, b[126]);
            Assert.Equal(0x22, b[637]);
        }

        [Fact]
        public void SyncPacket_MatchesSpecOffsets()
        {
            var packet = new SACNPacket(RootLayer.CreateRootLayerSync(Cid, 0x42, 0xF000));

            var b = Write(packet);

            Assert.Equal(49, b.Length);
            Assert.Equal(0x7000 | (49 - 16), U16(b, 16));
            Assert.Equal(0x00000008u, U32(b, 18));
            Assert.Equal(0x7000 | (49 - 38), U16(b, 38));
            Assert.Equal(0x00000001u, U32(b, 40));
            Assert.Equal(0x42, b[44]);
            Assert.Equal(0xF000, U16(b, 45));
            Assert.Equal(0x0000, U16(b, 47));
        }
    }
}
