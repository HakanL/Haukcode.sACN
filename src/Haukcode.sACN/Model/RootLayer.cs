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

        private byte[] GuidToByteArray(Guid input)
        {
            var bytes = input.ToByteArray();

            return new byte[] {
                bytes[3],
                bytes[2],
                bytes[1],
                bytes[0],

                bytes[5],
                bytes[4],

                bytes[7],
                bytes[6],

                bytes[8],
                bytes[9],

                bytes[10],
                bytes[11],
                bytes[12],
                bytes[13],
                bytes[14],
                bytes[15]
            };
        }

        private static Guid ByteArrayToGuid(byte[] input)
        {
            return new Guid(new byte[] {
                input[3],
                input[2],
                input[1],
                input[0],

                input[5],
                input[4],

                input[7],
                input[6],

                input[8],
                input[9],

                input[10],
                input[11],
                input[12],
                input[13],
                input[14],
                input[15]
            });
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
                buffer.Write(GuidToByteArray(UUID));

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
            Guid cid = ByteArrayToGuid(buffer.ReadBytes(16));

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
