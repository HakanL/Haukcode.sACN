using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Haukcode.sACN.Model
{
    /// <summary>
    /// The E1.31 Universe Discovery framing layer together with the Universe Discovery Layer
    /// it carries (E1.31-2018 Table 4-3).
    /// </summary>
    public class UniverseDiscoveryFramingLayer : FramingLayer
    {
        public const int SourceNameLength = 64;
        public const int ReservedLength = 4;
        public const uint VECTOR_UNIVERSE_DISCOVERY_UNIVERSE_LIST = 0x00000001;
        public const int MaxUniversesPerPage = 512;

        // Flags and Length, Vector, Source Name, Reserved
        private const int FramingHeaderLength = 2 + 4 + SourceNameLength + ReservedLength;

        // Flags and Length, Vector, Page, Last Page
        private const int DiscoveryHeaderLength = 2 + 4 + 1 + 1;

        public override ushort Length { get { return (ushort)(FramingHeaderLength + DiscoveryLayerLength); } }

        private ushort DiscoveryLayerLength => (ushort)(DiscoveryHeaderLength + (Universes.Length * 2));

        public string SourceName { get; set; } = null!;

        public byte Page { get; set; }

        public byte LastPage { get; set; }

        public ushort[] Universes { get; set; } = Array.Empty<ushort>();

        public override uint RootVector => RootLayer.VECTOR_ROOT_E131_EXTENDED;

        public UniverseDiscoveryFramingLayer(string sourceName, IEnumerable<ushort> universes, byte page = 0, byte lastPage = 0)
            : base(0)
        {
            SourceName = sourceName;
            Universes = universes?.ToArray() ?? [];
            Page = page;
            LastPage = lastPage;
        }

        public UniverseDiscoveryFramingLayer()
            : base(0)
        {
        }

        public override int WriteToBuffer(Memory<byte> buffer)
        {
            if (Universes.Length > MaxUniversesPerPage)
                throw new InvalidOperationException($"A universe discovery page holds at most {MaxUniversesPerPage} universes, got {Universes.Length}");

            var writer = new SpanBinaryWriter(buffer.Span);

            ushort flagsAndFramingLength = (ushort)(SACNPacket.FLAGS | Length);
            writer.WriteUInt16(flagsAndFramingLength);
            writer.WriteUInt32(VECTOR_E131_EXTENDED_DISCOVERY);
            writer.WriteString(SourceName, SourceNameLength);
            writer.WriteUInt32(0);

            // Universe Discovery Layer
            ushort flagsAndDiscoveryLength = (ushort)(SACNPacket.FLAGS | DiscoveryLayerLength);
            writer.WriteUInt16(flagsAndDiscoveryLength);
            writer.WriteUInt32(VECTOR_UNIVERSE_DISCOVERY_UNIVERSE_LIST);
            writer.WriteByte(Page);
            writer.WriteByte(LastPage);

            foreach (var universe in Universes)
            {
                writer.WriteUInt16(universe);
            }

            return writer.BytesWritten;
        }

        /// <summary>
        /// Parse the rest of the layer; RootLayer has read and checked its flags, length and vector.
        /// </summary>
        internal static UniverseDiscoveryFramingLayer Parse(BigEndianBinaryReader reader, int framingLength)
        {
            if (framingLength < FramingHeaderLength + DiscoveryHeaderLength)
                throw new InvalidDataException("Truncated universe discovery framing layer");

            string sourceName = reader.ReadString(SourceNameLength);
            reader.ReadUInt32();

            int discoveryLength = SACNPacket.ReadPduLength(reader.ReadUInt16(), "universe discovery layer");
            if (discoveryLength != framingLength - FramingHeaderLength)
                throw new InvalidDataException($"Universe discovery layer length {discoveryLength} doesn't match the framing layer");

            uint discoveryVector = reader.ReadUInt32();
            if (discoveryVector != VECTOR_UNIVERSE_DISCOVERY_UNIVERSE_LIST)
                throw new InvalidDataException($"Unknown universe discovery vector {discoveryVector}");

            byte page = reader.ReadByte();
            byte lastPage = reader.ReadByte();

            int universeBytes = discoveryLength - DiscoveryHeaderLength;
            if (universeBytes < 0 || (universeBytes % 2) != 0 || universeBytes > MaxUniversesPerPage * 2)
                throw new InvalidDataException("Invalid universe discovery payload length");

            var universes = new ushort[universeBytes / 2];
            for (int i = 0; i < universes.Length; i++)
            {
                universes[i] = reader.ReadUInt16();
            }

            return new UniverseDiscoveryFramingLayer
            {
                SourceName = sourceName,
                Page = page,
                LastPage = lastPage,
                Universes = universes
            };
        }
    }
}
