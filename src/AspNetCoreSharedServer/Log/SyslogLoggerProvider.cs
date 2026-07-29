using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SyslogNet.Client.Serialization;
using SyslogNet.Client.Transport;

namespace AspNetCoreSharedServer.Log;


public class SyslogLoggerProvider : ILoggerProvider
{
    private readonly ISyslogSender _sender;
    private readonly ISyslogMessageSerializer _serializer;

    public SyslogLoggerProvider(
        ProtocolType transport,
        string? host = null,
        int port = 514)
    {
        _serializer = new SyslogRfc5424MessageSerializer();

        _sender = transport switch
        {
            ProtocolType.Udp => new UdpSyslogSender(host ?? "localhost", port),
            ProtocolType.Tcp => new TcpSyslogSender(host ?? "localhost", port),
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };
    }

    public ILogger CreateLogger(string categoryName)
        => new SyslogLogger(_sender, _serializer, categoryName);

    public void Dispose() { }
}