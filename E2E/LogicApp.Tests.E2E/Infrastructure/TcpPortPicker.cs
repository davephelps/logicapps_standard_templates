using System.Net.Sockets;
using System.Net;

namespace LogicApp.Tests.E2E.Infrastructure;

internal static class TcpPortPicker
{
    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
