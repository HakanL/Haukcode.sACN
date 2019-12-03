using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace kadmium_sacn_core
{
    public class BigEndianBinaryReader : BinaryReader
    {
        public BigEndianBinaryReader(Stream input) : base(input)
        {

        }

        public BigEndianBinaryReader(Stream input, Encoding encoding) : base(input, encoding)
        {

        }

        public override short ReadInt16()
        {
            byte[] bytes = base.ReadBytes(2);
            short converted = BitConverter.ToInt16(bytes, 0);
            return System.Net.IPAddress.NetworkToHostOrder(converted);
        }

        public override ushort ReadUInt16()
        {
            byte[] bytes = base.ReadBytes(2);
            ushort converted = BitConverter.ToUInt16(bytes, 0);
            return (ushort)System.Net.IPAddress.NetworkToHostOrder((short)converted);
        }

        public override int ReadInt32()
        {
            byte[] bytes = base.ReadBytes(4);
            int converted = BitConverter.ToInt32(bytes, 0);
            return System.Net.IPAddress.NetworkToHostOrder(converted); ;
        }
    }
}