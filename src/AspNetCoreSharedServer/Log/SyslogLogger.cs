using Microsoft.Extensions.Logging;
using SyslogNet.Client;
using SyslogNet.Client.Serialization;
using SyslogNet.Client.Transport;


namespace AspNetCoreSharedServer.Log;

public interface ISyslogSender
{
    void Send(SyslogMessage message, ISyslogMessageSerializer serializer);
}
public class UdpSyslogSender : ISyslogSender
{
    private readonly SyslogUdpSender _sender;

    public UdpSyslogSender(string host, int port)
    {
        _sender = new SyslogUdpSender(host, port);
    }

    public void Send(SyslogMessage message, ISyslogMessageSerializer serializer)
        => _sender.Send(message, serializer);
}

public class TcpSyslogSender : ISyslogSender
{
    private readonly SyslogTcpSender _sender;

    public TcpSyslogSender(string host, int port)
    {
        _sender = new SyslogTcpSender(host, port);
    }

    public void Send(SyslogMessage message, ISyslogMessageSerializer serializer)
        => _sender.Send(message, serializer);
}
public class SyslogLogger : ILogger
{
    private readonly ISyslogSender _sender;
    private readonly ISyslogMessageSerializer _serializer;
    private readonly string _category;

    public SyslogLogger(
        ISyslogSender sender,
        ISyslogMessageSerializer serializer,
        string category)
    {
        _sender = sender;
        _serializer = serializer;
        _category = category;
    }

    public IDisposable BeginScope<TState>(TState state) => default!;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var msg = formatter(state, exception);

        var syslog = new SyslogMessage(
            DateTimeOffset.Now,
            MapFacility(logLevel),
            MapSeverity(logLevel),
            Environment.MachineName,
            "MyApp",
            null,
            eventId.Id.ToString(),
            msg);

        _sender.Send(syslog, _serializer);
    }

    private static Facility MapFacility(LogLevel level)
        => Facility.SystemDaemons;

    private static Severity MapSeverity(LogLevel level)
        => level switch
        {
            LogLevel.Trace => Severity.Debug,
            LogLevel.Debug => Severity.Debug,
            LogLevel.Information => Severity.Informational,
            LogLevel.Warning => Severity.Warning,
            LogLevel.Error => Severity.Error,
            LogLevel.Critical => Severity.Critical,
            _ => Severity.Informational
        };
}
