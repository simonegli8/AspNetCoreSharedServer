using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace AspNetCoreSharedServer;


public interface ICommandPipe
{
    Task StartAsync(Func<string, Task<string>> onMessage, CancellationToken ct);
}

public class NamedPipe : ICommandPipe
{
    private const string PipeName = "AspNetCoreSharedServerCommandPipe";

    public async Task StartAsync(Func<string, Task<string>> onMessage, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                10,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                1024,
                1024);

            await server.WaitForConnectionAsync(ct);

            using var reader = new StreamReader(server, Encoding.UTF8);
            using var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true };

            var msg = await reader.ReadLineAsync();
            if (msg is null) continue;

            var response = await onMessage(msg);
            await writer.WriteLineAsync(response);
        }
    }
}

public class UnixPipe : ICommandPipe
{
    private readonly string _path = "/tmp/aspnet-shared-server.sock";

    public async Task StartAsync(Func<string, Task<string>> onMessage, CancellationToken ct)
    {
        if (File.Exists(_path))
            File.Delete(_path);

        var endpoint = new UnixDomainSocketEndPoint(_path);
        File.SetUnixFileMode(_path, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(endpoint);
        listener.Listen(5);

        while (!ct.IsCancellationRequested)
        {
            var socket = await listener.AcceptAsync(ct);

            _ = Task.Run(async () =>
            {
                using (socket);
                using var stream = new NetworkStream(socket);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var msg = await reader.ReadLineAsync();
                if (msg is null) return;

                var response = await onMessage(msg);
                await writer.WriteLineAsync(response);
            }, ct);
        }
    }
}

public class CommandPipe
{
    public async Task<string> OnMessage(string msg)
    {
        var firstSpace = msg.IndexOf(' ');
        string command, argument;
        if (firstSpace == -1)
        {
            command = msg;
            argument = string.Empty;
        } else
        {
            command = msg.Substring(0, firstSpace);
            argument = msg.Substring(firstSpace + 1).Trim();
        }
        switch (command)
        {
            case nameof(Command.Version):
                var assembly = Assembly.GetExecutingAssembly();
                return (assembly?.GetName()?.Version ?? default).ToString();
            case nameof(Command.Stop): break;
            case nameof(Command.Start): break;
            case nameof(Command.Status):
                
                break;



                return Configuration.Current.Version.ToString(),
        }
    }

}
