using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Haukcode.sACN
{
    public class ReceiveDataRaw : ReceiveDataBase
    {
        public byte[] Data { get; set; }
    }
}
