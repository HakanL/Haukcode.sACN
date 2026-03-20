using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Haukcode.sACN.Model
{
    public class RootLayer
    {
        public const short PREAMBLE_LENGTH = 0x0010;
        public const short POSTAMBLE_LENGTH = 0x0000;
        public static readonly byte[] PACKET_IDENTIFIER = new byte[] {
            0x41, 0x53, 0x43, 0x2d, 0x45,
            0x31, 0x2e, 0x31, 0x37, 0x00,
            0x00, 0x00};
        public const int VECTOR_ROOT_E131_DATA = 0x00000004;
        public const int VECTOR_ROOT_E131_EXTENDED = 0x00000008;

        public FramingLayer FramingLayer { get; set; } = null!;

        public short Length { get { return (short)(38 + FramingLayer.Length); } }

        public Guid UUID { get; set; }

        public RootLayer()
        {
        }

        public static RootLayer CreateRootLayerData(Guid uuid, string sourceName, ushort universeID, byte sequenceID, ReadOnlyMemory<byte> data, byte priority, ushort syncAddress, byte startCode = 0)
        {
            return new RootLayer
            {
                UUID = uuid,
                FramingLayer = new DataFramingLayer(sourceName, universeID, sequenceID, data, priority, syncAddress, startCode)
            };
        }

        public static RootLayer CreateRootLayerSync(Guid uuid, byte sequenceID, ushort syncAddress)
        {
            return new RootLayer
            {
                UUID = uuid,
                FramingLayer = new SyncFramingLayer(syncAddress, sequenceID)
            };
        }

        public static RootLayer CreateRootLayerUniverseDiscovery(Guid uuid, string sourceName, ushort[] universes, byte page = 0, byte lastPage = 0)
        {
            return new RootLayer
            {
                UUID = uuid,
                FramingLayer = new UniverseDiscoveryFramingLayer(sourceName, universes, page, lastPage)
            };
        }

        public int WriteToBuffer(Memory<byte> buffer)
        {
            var writer = new BigEndianBinaryWriter(buffer);

            writer.WriteInt16(PREAMBLE_LENGTH);
            writer.WriteInt16(POSTAMBLE_LENGTH);
            writer.WriteBytes(PACKET_IDENTIFIER);
            ushort flagsAndRootLength = (ushort)(SACNPacket.FLAGS | (ushort)(Length - 16));
            writer.WriteUInt16(flagsAndRootLength);
            writer.WriteInt32(FramingLayer.RootVector);
            writer.WriteGuid(UUID);

            return writer.BytesWritten + FramingLayer.WriteToBuffer(writer.Memory);
        }

        internal static RootLayer Parse(BigEndianBinaryReader reader)
        {
            short preambleLength = reader.ReadInt16();
            if (preambleLength != PREAMBLE_LENGTH)
                throw new InvalidDataException("preambleLength != PREAMBLE_LENGTH");

            short postambleLength = reader.ReadInt16();
            if (postambleLength != POSTAMBLE_LENGTH)
                throw new InvalidDataException("postambleLength != POSTAMBLE_LENGTH");

            if (!reader.VerifyBytes(PACKET_IDENTIFIER))
                throw new InvalidDataException("packetIdentifier != PACKET_IDENTIFIER");

            ushort flagsAndRootLength = (ushort)reader.ReadInt16();
            ushort flags = (ushort)(flagsAndRootLength & SACNPacket.FIRST_FOUR_BITS_MASK);
            if (flags != SACNPacket.FLAGS)
                throw new InvalidDataException("flags != SACNPacket.FLAGS");

            ushort length = (ushort)(flagsAndRootLength & SACNPacket.LAST_TWELVE_BITS_MASK);
            int vector = reader.ReadInt32();
            Guid cid = reader.ReadGuid();

            switch (vector)
            {
                case VECTOR_ROOT_E131_DATA:
                    return new RootLayer
                    {
                        UUID = cid,
                        FramingLayer = DataFramingLayer.Parse(reader)
                    };

                case VECTOR_ROOT_E131_EXTENDED:
                    {
                        ushort flagsAndFramingLength = reader.ReadUInt16();
                        int framingVector = reader.ReadInt32();

                        FramingLayer framingLayer = framingVector switch
                        {
                            FramingLayer.VECTOR_E131_EXTENDED_SYNCHRONIZATION => SyncFramingLayer.Parse(reader, flagsAndFramingLength, framingVector),
                            FramingLayer.VECTOR_E131_EXTENDED_DISCOVERY => UniverseDiscoveryFramingLayer.Parse(reader, flagsAndFramingLength, framingVector),
                            _ => throw new ArgumentException($"Unknown extended framing vector {framingVector}")
                        };

                        return new RootLayer
                        {
                            UUID = cid,
                            FramingLayer = framingLayer
                        };
                    }

                default:
                    throw new ArgumentException($"Unknown vector {vector}");
            }
        }
    }
}
