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

        public FramingLayer FramingLayer { get; set; }

        public short Length { get { return (short)(38 + FramingLayer.Length); } }

        public Guid UUID { get; set; }

        public RootLayer()
        {
        }

        public static RootLayer CreateRootLayerData(Guid uuid, string sourceName, ushort universeID, byte sequenceID, byte[] data, byte priority, ushort syncAddress, byte startCode = 0)
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

        public byte[] ToArray()
        {
            using (var stream = new MemoryStream(Length))
            using (var buffer = new BigEndianBinaryWriter(stream))
            {
                buffer.Write(PREAMBLE_LENGTH);
                buffer.Write(POSTAMBLE_LENGTH);
                buffer.Write(PACKET_IDENTIFIER);
                ushort flagsAndRootLength = (ushort)(SACNPacket.FLAGS | (ushort)(Length - 16));
                buffer.Write(flagsAndRootLength);
                buffer.Write(FramingLayer.RootVector);
                buffer.Write(UUID.ToByteArray());

                buffer.Write(FramingLayer.ToArray());

                return stream.ToArray();
            }
        }

        internal static RootLayer Parse(BigEndianBinaryReader buffer)
        {
            short preambleLength = buffer.ReadInt16();
            if (preambleLength != PREAMBLE_LENGTH)
                throw new InvalidDataException("preambleLength != PREAMBLE_LENGTH");

            short postambleLength = buffer.ReadInt16();
            if (postambleLength != POSTAMBLE_LENGTH)
                throw new InvalidDataException("postambleLength != POSTAMBLE_LENGTH");

            byte[] packetIdentifier = buffer.ReadBytes(12);
            if (!packetIdentifier.SequenceEqual(PACKET_IDENTIFIER))
                throw new InvalidDataException("packetIdentifier != PACKET_IDENTIFIER");

            ushort flagsAndRootLength = (ushort)buffer.ReadInt16();
            ushort flags = (ushort)(flagsAndRootLength & SACNPacket.FIRST_FOUR_BITS_MASK);
            if (flags != SACNPacket.FLAGS)
                throw new InvalidDataException("flags != SACNPacket.FLAGS");

            ushort length = (ushort)(flagsAndRootLength & SACNPacket.LAST_TWELVE_BITS_MASK);
            int vector = buffer.ReadInt32();
            Guid cid = new Guid(buffer.ReadBytes(16));

            switch (vector)
            {
                case VECTOR_ROOT_E131_DATA:
                    return new RootLayer
                    {
                        UUID = cid,
                        FramingLayer = DataFramingLayer.Parse(buffer)
                    };

                case VECTOR_ROOT_E131_EXTENDED:
                    return new RootLayer
                    {
                        UUID = cid,
                        FramingLayer = SyncFramingLayer.Parse(buffer)
                    };

                default:
                    throw new ArgumentException($"Unknown vector {vector}");
            }
        }
    }
}
