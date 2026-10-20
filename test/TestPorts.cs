using System.Net;
using System.Net.Sockets;

namespace Haukcode.sACN.Test
{
    internal static class TestPorts
    {
        /// <summary>
        /// A UDP port nothing is using, picked by the OS. A random port could land in a range
        /// Windows reserves (Hyper-V) or on another test running in parallel.
        /// </summary>
        public static int GetFreeUdpPort()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
    }
}
