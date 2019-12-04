using System;
using System.IO;

namespace Haukcode.sACN
{
    internal class BigEndianBinaryWriter : BinaryWriter
    {
        public BigEndianBinaryWriter(Stream output)
            : base(output)
        {
        }

        public override void Write(short value)
        {
            short networkOrder = System.Net.IPAddress.HostToNetworkOrder(value);
            base.Write(networkOrder);
        }

        public override void Write(ushort value)
        {
            short networkOrder = System.Net.IPAddress.HostToNetworkOrder((short)value);
            base.Write(networkOrder);
        }

        public override void Write(int value)
        {
            int networkOrder = System.Net.IPAddress.HostToNetworkOrder(value);
            base.Write(networkOrder);
        }
    }
}
