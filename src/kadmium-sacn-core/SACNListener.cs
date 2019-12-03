using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace kadmium_sacn_core
{
    public class SACNListener
    {
        private Socket Socket { get; set; }
        public event EventHandler<SACNPacket> OnPacket;
        private Task ListenTask { get; set; }
        private CancellationToken Token { get; set; }

        public SACNListener(ushort universeID)
        {
            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            IPAddress localAddress = IPAddress.Any;
            EndPoint endPoint = new IPEndPoint(localAddress, SACNCommon.SACN_PORT);
            Socket.Bind(endPoint);
            
            MulticastOption option = new MulticastOption(SACNCommon.GetMulticastAddress(universeID), localAddress);
            Socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
            Token = new CancellationToken();

            ListenTask = Task.Factory.StartNew(() =>
            {
                byte[] buffer = new byte[SACNPacket.MAX_PACKET_SIZE];
                EndPoint remoteEP = new IPEndPoint(SACNCommon.GetMulticastAddress(universeID) , SACNCommon.SACN_PORT);

                ArraySegment<byte> segment = new ArraySegment<byte>(buffer);
                while(!Token.IsCancellationRequested)
                {
                    if(Socket.Available > 0)
                    {
                        try
                        {
                            int bytesReceived = Socket.ReceiveFrom(buffer, ref remoteEP);
                            SACNPacket packet = SACNPacket.Parse(segment.Array);
                            OnPacket?.Invoke(this, packet);
                        }
                        catch (SocketException e)
                        {
                            Console.Error.WriteLine(e.ToString());
                            Console.Error.WriteLine(e.Message);
                            Console.Error.WriteLine(e.StackTrace);
                        }
                    }
                }
            });
        }
    }
}
