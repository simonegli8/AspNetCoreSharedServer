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
| `AltSvcTests` | The `HTTP3_ALT_SVC_HEADER` / `HTTP3_ENABLED` values and how they reach the app (environment and sudo command line quoting). |
| `AppEnvironmentTests` | The environment of an app: its own variables, the ones the server defines and an app cannot override, `ASPNET_SERVER` (the version of the server) and `PROXY_PROTOCOL` (`v1`/`v2`, only when a PROXY header is sent), including how it travels on the sudo command line. |
| `ProxyHttp3MiddlewareTests` / `ProxyProtocolMiddlewareTests` | `UseAspNetCoreSharedServer` of the Middleware package: the `Alt-Svc` header, and a real Kestrel that reads v1 and v2 PROXY headers only when `PROXY_PROTOCOL` is set. |
| `PortAndProxyTests` | A port that is free for TCP and UDP, and HTTP/3 being switched off when a PROXY header is enabled. |
| `UnixTests` / `MacOsUnixTests` | `Unix.IsUserInGroup`, which decides which groups an app owner may use, and `UserExists`/`GroupExists`. `UnixTests` reads /etc/passwd and runs on Linux only; `MacOsUnixTests` uses accounts every Mac has and runs on macOS only. |
| `UnixStatTests` | How the owner of a file is read from the stat record of the operating system: the layouts of macOS (`UnixMac`), Linux x86_64 (`UnixX64`), Linux arm64 (`UnixArm`) and `statx`, built byte by byte so that they are tested on every machine, a wrong layout being refused instead of believed, and (on Linux and macOS) `Unix.GetOwnerAndGroup` against `stat(1)`. |
| `WindowsAccountsTests` | Windows: local users and groups (`UserExists`, `GroupExists`, `IsUserInGroup`), the environment block of an app (the server's variables win) and the check for the privileges of an S4U launch. Read-only, no administrator rights needed. Skipped elsewhere. |
| `WindowsFileSecurityTests` / `WindowsAppFileLoadTests` | Windows: who may change an app's json file, read from its permissions (user, administrators and SYSTEM only), the permissions the server sets, and loading `configuration.json` with app files: an insecure file, or a group that the user is not in, takes the app offline; an insecure `configuration.json` is refused; saving as an administrator protects the folder and the file. No administrator rights needed. |
| `WindowsLaunchTests` | **Opt-in, needs LocalSystem.** The real S4U launch: creates a local user, starts a process as it, checks who it is and which variables it got, and that ending the launch ends the app. See below. |
| `SudoArgumentsTests` | The sudo command line with which the server starts an app as another user (Linux and macOS): `-H -u user [-g group] NAME=value... -- command`, with hostile values kept inside their own argument. |
| `SudoLaunchTests` | **Needs root.** Starts a real process through that command line and checks from the inside that it is the other user completely (real and effective id) and cannot switch back to root. See below. |

Tests that cannot run on a machine (no IPv6 loopback, not Linux, ...) are reported as skipped, not as passed.

## The root-only test

`SudoLaunchTests` protects the privilege drop: an app must not be able to get root back. Only root can start a process as another
user, so the test is skipped for everybody else. Run the suite as root on Linux or macOS to include it:

```
sudo dotnet test src/AspNetCoreSharedServer.Tests
```

It needs `sudo`, `perl` (as the probe program) and the user `nobody`, which all standard Linux and macOS systems have. On macOS
this is the only place where the privilege drop is checked, so run it once on a Mac.

## The Windows S4U test (opt-in)

`WindowsLaunchTests` start a process as another local user, which only an account with the privilege "Act as part of the operating
system" can do, and they create (and delete) a local account. They run only when asked, as LocalSystem:

```
set ASPNETSERVER_S4U=1
dotnet test src/AspNetCoreSharedServer.Tests --filter Category=Windows-S4U
```

`LocalSystem` is not your login: run the command from a scheduled task that runs as SYSTEM, or with a tool like PsExec (`psexec -s`).

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
- **Windows, elevated.** The server is told where its configuration is with `ASPNETSERVER_CONFIG_DIR`, so the tests run it in a temporary
  folder and never touch a real configuration. The server only loads a `configuration.json` that administrators alone can change, so it
  (and the test) must run as an administrator. On Linux the path is `/etc/aspnet-server`, which a test must not change.
- QUIC support in the operating system (Windows 11 / Server 2022 or newer).
- The ASP.NET Core HTTPS development certificate: `dotnet dev-certs https`.
