using Haukcode.sACN;
using System;
using System.Threading;

namespace Haukcode.sACN.ConsoleExample
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Listen();
        }

        static void Listen()
        {
            var listener = new SACNListener(1);
            listener.OnPacket += Listener_OnPacket;
            while (true)
            {
                Thread.Sleep(100);
            }
        }

        private static void Listener_OnPacket(object sender, SACNPacket e)
        {
            Console.WriteLine("Packet from " + e.SourceName);
            Console.WriteLine("\tUniverseID = " + e.UniverseID);
            Console.WriteLine("\tSequenceID = " + e.SequenceID);
            Console.WriteLine("\tData = " + string.Join(",", e.Data));
        }

        static void Send()
        {
            var sender = new SACNSender(Guid.NewGuid(), "kadmium-sacn-core");
            byte[] data =
            {
                1, 2, 3, 4, 5, 255
            };
            while (true)
            {
                sender.Send(1, data).Wait();
                Console.WriteLine("Sent packet");
                Thread.Sleep(100);
            }
        }
    }
}
