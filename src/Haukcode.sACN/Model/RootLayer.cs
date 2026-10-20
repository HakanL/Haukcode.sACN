using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Haukcode.sACN.Model
{
    public class RootLayer
    {
        public const ushort PREAMBLE_LENGTH = 0x0010;
        public const ushort POSTAMBLE_LENGTH = 0x0000;
        public static readonly byte[] PACKET_IDENTIFIER = new byte[] {
            0x41, 0x53, 0x43, 0x2d, 0x45,
            0x31, 0x2e, 0x31, 0x37, 0x00,
            0x00, 0x00};
        public const uint VECTOR_ROOT_E131_DATA = 0x00000004;
        public const uint VECTOR_ROOT_E131_EXTENDED = 0x00000008;

        // Preamble, postamble, identifier, flags and length, vector, CID
        internal const int HeaderLength = 38;

        public FramingLayer FramingLayer { get; set; } = null!;

        public ushort Length { get { return (ushort)(HeaderLength + FramingLayer.Length); } }

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
            var writer = new SpanBinaryWriter(buffer.Span);

            writer.WriteUInt16(PREAMBLE_LENGTH);
            writer.WriteUInt16(POSTAMBLE_LENGTH);
            writer.WriteBytes(PACKET_IDENTIFIER);
            ushort flagsAndRootLength = (ushort)(SACNPacket.FLAGS | (ushort)(Length - 16));
            writer.WriteUInt16(flagsAndRootLength);
            writer.WriteUInt32(FramingLayer.RootVector);
            writer.WriteGuid(UUID);

            return writer.BytesWritten + FramingLayer.WriteToBuffer(buffer.Slice(writer.BytesWritten));
        }

        /// <summary>
        /// Parse, filling <paramref name="scratchRootLayer"/> in place when the packet is a DATA
        /// packet and the scratch carries a DataFramingLayer (see SACNPacket.Parse). Other packet
        /// types allocate as before. Returns null for a datagram that isn't an E1.31 packet, or
        /// uses a root or extended framing vector we don't know: E1.31 says to discard those.
        /// Throws InvalidDataException for a packet that claims to be E1.31 but is malformed.
        /// </summary>
        internal static RootLayer? Parse(BigEndianBinaryReader reader, int datagramLength, RootLayer? scratchRootLayer)
        {
            if (datagramLength < 16)
                return null;

            ushort preambleLength = reader.ReadUInt16();
            ushort postambleLength = reader.ReadUInt16();

            // Span compare and span-constructed Guid: the reader's VerifyBytes/ReadGuid copy the
            // bytes into fresh arrays, which was two allocations on every received packet
            if (preambleLength != PREAMBLE_LENGTH ||
                postambleLength != POSTAMBLE_LENGTH ||
                !reader.ReadSlice(PACKET_IDENTIFIER.Length).Span.SequenceEqual(PACKET_IDENTIFIER))
            {
                return null;
            }

            if (datagramLength < HeaderLength)
                throw new InvalidDataException("Truncated root layer");

            int rootLength = SACNPacket.ReadPduLength(reader.ReadUInt16(), "root layer");
            if (rootLength < HeaderLength - 16 || rootLength > datagramLength - 16)
                throw new InvalidDataException($"Root layer length {rootLength} doesn't fit a {datagramLength} byte datagram");

            uint vector = reader.ReadUInt32();
            Guid cid = new Guid(reader.ReadSlice(16).Span, bigEndian: true);

            int framingLength = rootLength - (HeaderLength - 16);

            switch (vector)
            {
                case VECTOR_ROOT_E131_DATA:
                    if (scratchRootLayer?.FramingLayer is DataFramingLayer scratchFramingLayer)
                    {
                        scratchRootLayer.UUID = cid;
                        DataFramingLayer.Parse(reader, framingLength, scratchFramingLayer);

                        return scratchRootLayer;
                    }

                    return new RootLayer
                    {
                        UUID = cid,
                        FramingLayer = DataFramingLayer.Parse(reader, framingLength, null)
                    };

                case VECTOR_ROOT_E131_EXTENDED:
                    {
                        if (framingLength < 6)
                            throw new InvalidDataException("Truncated extended framing layer");

                        int framingPduLength = SACNPacket.ReadPduLength(reader.ReadUInt16(), "framing layer");
                        if (framingPduLength != framingLength)
                            throw new InvalidDataException($"Framing layer length {framingPduLength} doesn't match the root layer ({framingLength})");

                        uint framingVector = reader.ReadUInt32();

                        FramingLayer? framingLayer = framingVector switch
                        {
                            FramingLayer.VECTOR_E131_EXTENDED_SYNCHRONIZATION => SyncFramingLayer.Parse(reader, framingLength),
                            FramingLayer.VECTOR_E131_EXTENDED_DISCOVERY => UniverseDiscoveryFramingLayer.Parse(reader, framingLength),
                            _ => null
                        };

                        if (framingLayer == null)
                            return null;

                        return new RootLayer
                        {
                            UUID = cid,
                            FramingLayer = framingLayer
                        };
                    }

                default:
                    return null;
            }
        }
    }
}
