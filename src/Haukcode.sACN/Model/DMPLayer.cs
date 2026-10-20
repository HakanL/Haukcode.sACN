using System;
using System.Diagnostics;
using System.IO;

namespace Haukcode.sACN.Model
{
    public class DMPLayer
    {
        public const byte DMP_VECTOR = 2;
        public const byte ADDRESS_TYPE_AND_DATA_TYPE = 0xA1;
        public const ushort FIRST_PROPERTY_ADDRESS = 0x00;
        public const ushort ADDRESS_INCREMENT = 1;
        public const int MaxSlots = 512;

        // Flags and Length, Vector, Address Type & Data Type, First Property Address, Address
        // Increment, Property Value Count; the start code and slots follow
        internal const int HeaderLength = 2 + 1 + 1 + 2 + 2 + 2;

        public byte StartCode { get; set; }

        public ushort Length { get { return (ushort)(HeaderLength + 1 + Data.Length); } }

        public ReadOnlyMemory<byte> Data { get; set; }

        public DMPLayer(ReadOnlyMemory<byte> data, byte startCode = 0x00)
        {
            Data = data;
            StartCode = startCode;
        }

        public int WriteToBuffer(Memory<byte> buffer)
        {
            if (Data.Length > MaxSlots)
                throw new InvalidOperationException($"A DMP layer holds at most {MaxSlots} slots, got {Data.Length}");

            var writer = new SpanBinaryWriter(buffer.Span);

            ushort flagsAndDMPLength = (ushort)(SACNPacket.FLAGS | Length);

            writer.WriteUInt16(flagsAndDMPLength);
            writer.WriteByte(DMP_VECTOR);
            writer.WriteByte(ADDRESS_TYPE_AND_DATA_TYPE);
            writer.WriteUInt16(FIRST_PROPERTY_ADDRESS);
            writer.WriteUInt16(ADDRESS_INCREMENT);
            writer.WriteUInt16((ushort)(Data.Length + 1));
            writer.WriteByte(StartCode);
            writer.WriteBytes(Data.Span);

            return writer.BytesWritten;
        }

        /// <summary>
        /// Parse, filling <paramref name="scratchLayer"/> in place when provided. Data is a slice
        /// of the reader's buffer either way.
        /// </summary>
        internal static DMPLayer Parse(BigEndianBinaryReader reader, int dmpLength, DMPLayer? scratchLayer)
        {
            if (dmpLength < HeaderLength + 1)
                throw new InvalidDataException("Truncated DMP layer");

            int dmpPduLength = SACNPacket.ReadPduLength(reader.ReadUInt16(), "DMP layer");
            if (dmpPduLength != dmpLength)
                throw new InvalidDataException($"DMP layer length {dmpPduLength} doesn't match the framing layer ({dmpLength})");

            byte vector = reader.ReadByte();
            if (vector != DMP_VECTOR)
                throw new InvalidDataException($"Unknown DMP vector {vector}");

            byte addressTypeAndDataType = reader.ReadByte();
            ushort firstPropertyAddress = reader.ReadUInt16();
            ushort addressIncrement = reader.ReadUInt16();
            if (addressTypeAndDataType != ADDRESS_TYPE_AND_DATA_TYPE || firstPropertyAddress != FIRST_PROPERTY_ADDRESS || addressIncrement != ADDRESS_INCREMENT)
                throw new InvalidDataException("Invalid DMP address type, first property address or address increment");

            // The start code plus up to 512 slots, and it has to fill the rest of the layer
            ushort propertyValueCount = reader.ReadUInt16();
            if (propertyValueCount < 1 || propertyValueCount > MaxSlots + 1 || propertyValueCount != dmpLength - HeaderLength)
                throw new InvalidDataException($"Invalid DMP property value count {propertyValueCount}");

            byte startCode = reader.ReadByte();
            var data = reader.ReadSlice(propertyValueCount - 1);

            if (scratchLayer != null)
            {
                scratchLayer.Data = data;
                scratchLayer.StartCode = startCode;

                return scratchLayer;
            }

            return new DMPLayer(data, startCode);
        }
    }
}
