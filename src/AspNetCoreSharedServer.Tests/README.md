# AspNetCoreSharedServer.Tests

xunit tests for the proxy and the HTTP/3 relay. They use plain loopback sockets, so they need no admin rights, no service and no
changes to the machine.

```
dotnet test src/AspNetCoreSharedServer.Tests
```

| Tests | What they cover |
|---|---|
| `UdpRelayTests` | The HTTP/3 (QUIC) relay: sessions per client, replies to the right client, the 1200 byte rule, the session limit, expiry, a restarted app, IPv4/IPv6 fallback, cancellation. |
| `TcpRelayTests` | The TCP copy between client and app: half-close, reset, the idle bound after a half-close, cancellation. |
| `ProxyHeaderTests` | PROXY protocol v1/v2 headers: the destination is the address and port the client connected to. |
| `AltSvcTests` | The `ALT_SVC_HEADER` / `HTTP3_ENABLED` values and how they reach the app (environment and sudo command line quoting). |
| `PortAndProxyTests` | A port that is free for TCP and UDP, and HTTP/3 being switched off when a PROXY header is enabled. |
| `UnixTests` | `Unix.IsUserInGroup`, which decides which groups an app owner may use. Linux only, skipped elsewhere. |

Tests that cannot run on a machine (no IPv6 loopback, not Linux, ...) are reported as skipped, not as passed.

## Running on Linux (or WSL)

The suite passes on Linux. If you work in WSL on a Windows checkout, do not run `dotnet test` in `/mnt/c/...`: it rewrites `obj/`
and `bin/` with Linux paths and breaks the next Windows build. Copy the sources into the Linux file system and test there:

```
mkdir -p ~/aspnet-tests/nupkg
tar -C /mnt/c/path/to/AspNetCoreSharedServer --exclude=./.git --exclude=./.vs --exclude=./nupkg --exclude=./nuget-apikey.txt \
    --exclude=bin --exclude=obj -cf - . | tar -C ~/aspnet-tests -xf -
cd ~/aspnet-tests && dotnet test src/AspNetCoreSharedServer.Tests
```

The empty `nupkg` folder is needed because the project files list it as a restore source, and NuGet treats a missing local
source as an error.

## End-to-end tests (opt-in)

`EndToEndHttp3Tests` start the real server with the real `AspNetCoreTestApp` behind it, and talk to it with a real HTTP/3
client: discovery through `Alt-Svc`, an app that dies and is restarted, and HTTP/3 traffic keeping the app from being shut
down as idle. They take about 40 seconds and only run when asked:

```
set ASPNETSERVER_E2E=1
dotnet test src/AspNetCoreSharedServer.Tests --filter Category=EndToEnd
```

They need:
- **Windows.** The server reads its configuration from the current directory there, so the tests run it in a temporary folder
  and never touch a real configuration. On Linux the path is `/etc/aspnet-server`, which a test must not change.
- QUIC support in the operating system (Windows 11 / Server 2022 or newer).
- The ASP.NET Core HTTPS development certificate: `dotnet dev-certs https`.
