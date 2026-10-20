using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
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

        [Fact]
        public void UniverseDiscoveryPacket_MatchesSpecOffsets()
        {
            var packet = new SACNUniverseDiscoveryPacket(Cid, "Src", new ushort[] { 1, 2, 0xF9FF }, page: 1, lastPage: 2);

            var b = Write(packet);

            // 120 header bytes + 2 per universe
            Assert.Equal(126, b.Length);
            Assert.Equal(0x7000 | (126 - 16), U16(b, 16));
            Assert.Equal(0x00000008u, U32(b, 18));

            // Framing layer
            Assert.Equal(0x7000 | (126 - 38), U16(b, 38));
            Assert.Equal(0x00000002u, U32(b, 40));
            Assert.Equal((byte)'S', b[44]);
            Assert.Equal(0u, U32(b, 108));

            // Universe discovery layer
            Assert.Equal(0x7000 | (126 - 112), U16(b, 112));
            Assert.Equal(0x00000001u, U32(b, 114));
            Assert.Equal(1, b[118]);
            Assert.Equal(2, b[119]);
            Assert.Equal(1, U16(b, 120));
            Assert.Equal(2, U16(b, 122));
            Assert.Equal(0xF9FF, U16(b, 124));
        }

        [Fact]
        public void UniverseDiscoveryPacket_RoundTrips()
        {
            var universes = new ushort[512];
            for (int i = 0; i < universes.Length; i++)
                universes[i] = (ushort)(i + 1);

            var b = Write(new SACNUniverseDiscoveryPacket(Cid, "Source Name", universes, page: 3, lastPage: 4));
            Assert.Equal(SACNPacket.MAX_UNIVERSE_DISCOVERY_PACKET_SIZE, b.Length);

            var parsed = Assert.IsType<SACNUniverseDiscoveryPacket>(SACNPacket.Parse(b));
            Assert.Equal(Cid, parsed.UUID);
            Assert.Equal("Source Name", parsed.SourceName);
            Assert.Equal(3, parsed.Page);
            Assert.Equal(4, parsed.LastPage);
            Assert.Equal(universes, parsed.Universes);
        }

        [Fact]
        public void UniverseDiscoveryPacket_RejectsMoreThan512Universes()
        {
            var packet = new SACNUniverseDiscoveryPacket(Cid, "Src", new ushort[513]);

            Assert.Throws<InvalidOperationException>(() => packet.WriteToBuffer(new byte[2000]));
        }

        [Fact]
        public void UniverseDiscovery_CreatePages_SortsAndPages()
        {
            var universes = new List<ushort>();
            for (int i = 1200; i >= 1; i--)
                universes.Add((ushort)i);
            universes.Add(5);

            var pages = SACNUniverseDiscoveryPacket.CreatePages(Cid, "Src", universes);

            Assert.Equal(3, pages.Count);
            Assert.All(pages, p => Assert.Equal(2, p.LastPage));
            Assert.Equal(new byte[] { 0, 1, 2 }, pages.Select(p => p.Page));
            Assert.Equal(new[] { 512, 512, 176 }, pages.Select(p => p.Universes.Length));
            Assert.Equal(Enumerable.Range(1, 1200).Select(i => (ushort)i), pages.SelectMany(p => p.Universes));
        }

        [Fact]
        public void UniverseDiscovery_CreatePages_EmptyListSendsOnePage()
        {
            var pages = SACNUniverseDiscoveryPacket.CreatePages(Cid, "Src", []);

            var page = Assert.Single(pages);
            Assert.Empty(page.Universes);
            Assert.Equal(0, page.LastPage);
        }

        [Fact]
        public void UniverseDiscoveryMulticastAddress_IsUniverse64214()
        {
            Assert.Equal(64214, SACNClient.UniverseDiscoveryUniverse);
            Assert.Equal(System.Net.IPAddress.Parse("239.255.250.214"), SACNClient.UniverseDiscoveryMulticastAddress);
        }
    }
}
