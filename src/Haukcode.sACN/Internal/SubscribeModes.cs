using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Haukcode.sACN;

[Flags]
public enum SubscribeModes
{
    None = 0,
    DmxData = 1,
    Trigger = 2
}
