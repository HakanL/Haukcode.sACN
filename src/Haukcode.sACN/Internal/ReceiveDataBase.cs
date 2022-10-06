using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Haukcode.sACN
{
    public abstract class ReceiveDataBase
    {
        public double TimestampMS { get; set; }

        public IPEndPoint Source { get; set; }
        
        public IPEndPoint Destination { get; set; }
    }
}
