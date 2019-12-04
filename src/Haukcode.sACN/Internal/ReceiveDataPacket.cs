using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Haukcode.sACN.Model;

namespace Haukcode.sACN
{
    public class ReceiveDataPacket : ReceiveDataBase
    {
        public SACNPacket Packet { get; set; }
    }
}
