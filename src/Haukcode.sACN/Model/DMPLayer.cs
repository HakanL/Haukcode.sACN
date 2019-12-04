using System;
using System.Diagnostics;
using System.IO;

namespace Haukcode.sACN.Model
{
    public class DMPLayer
    {
        private static readonly byte DMP_VECTOR = 2;
        private static readonly byte ADDRESS_TYPE_AND_DATA_TYPE = 0xA1;
        private static readonly short FIRST_PROPERTY_ADDRESS = 0x00;
        private static readonly short ADDRESS_INCREMENT = 1;

        public byte StartCode { get; set; }

        public short Length { get { return (short)(11 + Data.Length); } }

        public byte[] Data { get; set; }

        public DMPLayer(byte[] data, byte startCode = 0x00)
        {
            Data = data;
        }

        public byte[] ToArray()
        {
            using (var stream = new MemoryStream(Length))
            using (var buffer = new BigEndianBinaryWriter(stream))
            {
                ushort flagsAndDMPLength = (ushort)(SACNPacket.FLAGS | (ushort)Length);

                buffer.Write(flagsAndDMPLength);
                buffer.Write(DMP_VECTOR);
                buffer.Write(ADDRESS_TYPE_AND_DATA_TYPE);
                buffer.Write(FIRST_PROPERTY_ADDRESS);
                buffer.Write(ADDRESS_INCREMENT);
                buffer.Write((short)(Data.Length + 1));
                buffer.Write(StartCode);
                buffer.Write(Data);

                return stream.ToArray();
            }
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
