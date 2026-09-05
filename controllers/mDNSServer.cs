using System.Net;
using System.Net.Sockets;
using Makaretu.Dns;

namespace Biegove.controllers
{
    public class mDNSServer
    {
        private ServiceDiscovery? sd;
        private ServiceProfile? service;

        public void init(int port)
        {
            service = new ServiceProfile("Biegove", "_biegove._tcp", (ushort)port);

            foreach (var addr in MulticastService.GetIPAddresses())
            {
                if (addr.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr)) continue;

                service.Resources.Add(new ARecord
                {
                    Name = "biegove.local",
                    Address = addr
                });
            }

            sd = new ServiceDiscovery();
            sd.Advertise(service);
        }
    }
}
