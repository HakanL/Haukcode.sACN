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

        public byte[] Data { get; set; }

        public DMPLayer(byte[] data, byte startCode = 0x00)
        {
            Data = data;
            StartCode = startCode;
        }

        public int WriteToBuffer(Memory<byte> buffer)
        {
            var writer = new BigEndianBinaryWriter(buffer);

            ushort flagsAndDMPLength = (ushort)(SACNDataPacket.FLAGS | (ushort)Length);

            writer.WriteUShort(flagsAndDMPLength);
            writer.WriteByte(DMP_VECTOR);
            writer.WriteByte(ADDRESS_TYPE_AND_DATA_TYPE);
            writer.WriteShort(FIRST_PROPERTY_ADDRESS);
            writer.WriteShort(ADDRESS_INCREMENT);
            writer.WriteShort((short)(Data.Length + 1));
            writer.WriteByte(StartCode);
            writer.WriteBytes(Data);

            return writer.WrittenBytes;
        }

        internal static DMPLayer Parse(BigEndianBinaryReader buffer)
        {
            short flagsAndDMPLength = buffer.ReadInt16();
            byte vector3 = buffer.ReadByte();
            Debug.Assert(vector3 == DMP_VECTOR);
            byte addressTypeAndDataType = buffer.ReadByte();
            Debug.Assert(addressTypeAndDataType == ADDRESS_TYPE_AND_DATA_TYPE);
            short firstPropertyAddress = buffer.ReadInt16();
            Debug.Assert(firstPropertyAddress == FIRST_PROPERTY_ADDRESS);
            short addressIncrement = buffer.ReadInt16();
            Debug.Assert(addressIncrement == ADDRESS_INCREMENT);
            short propertyValueCount = buffer.ReadInt16();

            byte startCode = buffer.ReadByte();
            byte[] properties = buffer.ReadBytes(propertyValueCount - 1);

            var dmpLayer = new DMPLayer(properties, startCode);

            return dmpLayer;
        }
    }
}
