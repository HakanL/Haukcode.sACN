using System;
using System.Diagnostics;
using System.IO;

namespace Haukcode.sACN.Model
{
    public class DMPLayer
    {
        public const byte DMP_VECTOR = 2;
        public const byte ADDRESS_TYPE_AND_DATA_TYPE = 0xA1;
        public const short FIRST_PROPERTY_ADDRESS = 0x00;
        public const short ADDRESS_INCREMENT = 1;

        public byte StartCode { get; set; }

        public short Length { get { return (short)(11 + Data.Length); } }

        public ReadOnlyMemory<byte> Data { get; set; }

        public DMPLayer(ReadOnlyMemory<byte> data, byte startCode = 0x00)
        {
            Data = data;
            StartCode = startCode;
        }

        public int WriteToBuffer(Memory<byte> buffer)
        {
            var writer = new BigEndianBinaryWriter(buffer);

            ushort flagsAndDMPLength = (ushort)(SACNDataPacket.FLAGS | (ushort)Length);

            writer.WriteUInt16(flagsAndDMPLength);
            writer.WriteByte(DMP_VECTOR);
            writer.WriteByte(ADDRESS_TYPE_AND_DATA_TYPE);
            writer.WriteInt16(FIRST_PROPERTY_ADDRESS);
            writer.WriteInt16(ADDRESS_INCREMENT);
            writer.WriteInt16((short)(Data.Length + 1));
            writer.WriteByte(StartCode);
            writer.WriteBytes(Data);

            return writer.BytesWritten;
        }

        internal static DMPLayer Parse(BigEndianBinaryReader reader)
        {
            short flagsAndDMPLength = reader.ReadInt16();
            byte vector3 = reader.ReadByte();
            Debug.Assert(vector3 == DMP_VECTOR);
            byte addressTypeAndDataType = reader.ReadByte();
            Debug.Assert(addressTypeAndDataType == ADDRESS_TYPE_AND_DATA_TYPE);
            short firstPropertyAddress = reader.ReadInt16();
            Debug.Assert(firstPropertyAddress == FIRST_PROPERTY_ADDRESS);
            short addressIncrement = reader.ReadInt16();
            Debug.Assert(addressIncrement == ADDRESS_INCREMENT);
            short propertyValueCount = reader.ReadInt16();

            byte startCode = reader.ReadByte();
            if (propertyValueCount > 0)
            {
                var properties = reader.ReadSlice(propertyValueCount - 1);

                var dmpLayer = new DMPLayer(properties, startCode);

                return dmpLayer;
            }
            else
            {
                return new DMPLayer(Array.Empty<byte>());
            }
        }
    }
}
