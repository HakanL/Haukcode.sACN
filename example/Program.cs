using Haukcode.sACN;
using Haukcode.sACN.Model;
using System;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Threading;

namespace Haukcode.sACN.ConsoleExample
{
    public class Program
    {
        private static readonly Guid acnSourceId = new Guid("{B32625A6-C280-4389-BD25-E0D13F5B50E0}");
        private static readonly string acnSourceName = "DMXPlayer";

        public static void Main(string[] args)
        {
            Listen();
        }

        static void Listen()
        {
            var recvClient = new SACNClient(
                senderId: acnSourceId,
                senderName: acnSourceName,
                localAddress: IPAddress.Any);

            var sendClient = new SACNClient(
                senderId: acnSourceId,
                senderName: acnSourceName,
                localAddress: SACNCommon.GetFirstBindAddress());

            recvClient.OnError.Subscribe(e =>
            {
                Console.WriteLine($"Error! {e.Message}");
            });

            //listener.OnReceiveRaw.Subscribe(d =>
            //{
            //    Console.WriteLine($"Received {d.Data.Length} bytes from {d.Host}");
            //});

            double last = 0;
            recvClient.OnPacket.Subscribe(d =>
            {
                Listener_OnPacket(d.TimestampMS, d.TimestampMS - last, d.Packet);
                last = d.TimestampMS;
            });

            recvClient.StartReceive();
            recvClient.JoinDMXUniverse(1);
            recvClient.JoinDMXUniverse(2);

            while (true)
            {
                sendClient.SendMulticast(1, new byte[] { 1, 2, 3, 4, 5 });

                Thread.Sleep(500);
            }
        }

        private static void Listener_OnPacket(double timestampMS, double sinceLast, SACNPacket e)
        {
            var dataPacket = e as SACNDataPacket;
            if (dataPacket == null)
                return;

            Console.Write($"+{sinceLast:N2}\t");
            Console.Write($"Packet from {dataPacket.SourceName}\tu{dataPacket.UniverseId}\ts{dataPacket.SequenceId}\t");
            Console.WriteLine($"Data {string.Join(",", dataPacket.DMXData.Take(16))}...");
        }
    }
}
