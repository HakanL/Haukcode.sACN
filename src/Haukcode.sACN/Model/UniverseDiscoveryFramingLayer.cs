using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Haukcode.sACN.Model
{
    public class UniverseDiscoveryFramingLayer : FramingLayer
    {
        public const int SourceNameLength = 64;
        public const int ReservedLength = 4;

        public override ushort Length { get { return (ushort)(76 + (Universes.Length * 2)); } }

        public string SourceName { get; set; } = null!;

        public byte Page { get; set; }

        public byte LastPage { get; set; }

        public ushort[] Universes { get; set; } = Array.Empty<ushort>();

        public override int RootVector => RootLayer.VECTOR_ROOT_E131_EXTENDED;

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
            var writer = new BigEndianBinaryWriter(buffer);

            ushort flagsAndFramingLength = (ushort)(SACNPacket.FLAGS | Length);
            writer.WriteUInt16(flagsAndFramingLength);
            writer.WriteInt32(VECTOR_E131_EXTENDED_DISCOVERY);
            writer.WriteString(SourceName, SourceNameLength);
            writer.WriteInt32(0);
            writer.WriteByte(Page);
            writer.WriteByte(LastPage);

            foreach (var universe in Universes)
            {
                writer.WriteUInt16(universe);
            }

            return writer.BytesWritten;
        }

        internal static UniverseDiscoveryFramingLayer Parse(BigEndianBinaryReader reader, ushort flagsAndFramingLength, int vector)
        {
            ushort flags = (ushort)(flagsAndFramingLength & SACNPacket.FIRST_FOUR_BITS_MASK);
            Debug.Assert(flags == SACNPacket.FLAGS);
            Debug.Assert(vector == VECTOR_E131_EXTENDED_DISCOVERY);

            string sourceName = reader.ReadString(SourceNameLength);
            reader.ReadInt32();
            byte page = reader.ReadByte();
            byte lastPage = reader.ReadByte();

            int universeCount = (flagsAndFramingLength & SACNPacket.LAST_TWELVE_BITS_MASK) - 76;
            if (universeCount < 0 || (universeCount % 2) != 0)
                throw new InvalidDataException("Invalid universe discovery payload length");

            var universes = new ushort[universeCount / 2];
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
