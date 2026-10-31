using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AspNetCoreSharedServer;

/// <summary>
/// Starts an app as another local user on Windows, the counterpart of the <c>sudo -u</c> launch on Linux and macOS.
/// The user is logged on with S4U (<c>LsaLogonUser</c>, "service for user"), which needs no password, and the app is started with
/// that token through <c>CreateProcessAsUser</c>. The app is put in a job object, so that it ends with the server.
/// </summary>
/// <remarks>
/// The server has to hold the privileges <c>SeTcbPrivilege</c> (act as part of the operating system), <c>SeAssignPrimaryTokenPrivilege</c>
/// and <c>SeIncreaseQuotaPrivilege</c>. LocalSystem has them. A dedicated service account only has them if they are granted to it, and
/// then it can become any local user, so it is as powerful as LocalSystem. The token that an S4U logon returns has no network
/// credentials: the app cannot reach network shares as that user.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsLaunch : IDisposable
{
    /// <summary>The app's process. It is created suspended: call <see cref="Resume"/> once the events are hooked up.</summary>
    public Process Process { get; }

    readonly IntPtr token;
    readonly IntPtr profile;
    readonly IntPtr job;
    IntPtr thread;
    IntPtr processHandle;
    bool disposed;

    WindowsLaunch(Process process, IntPtr token, IntPtr profile, IntPtr job, IntPtr thread, IntPtr processHandle)
    {
        this.processHandle = processHandle;
        Process = process;
        this.token = token;
        this.profile = profile;
        this.job = job;
        this.thread = thread;
    }

    /// <summary>True if <paramref name="user"/> is the account that this process runs as, so that no other logon is needed.</summary>
    public static bool IsCurrentUser(string user)
    {
        var name = WindowsAccounts.LocalName(user);
        if (name == null) return false;
        var current = WindowsIdentity.GetCurrent().Name;
        return string.Equals(current.Substring(current.LastIndexOf('\\') + 1), name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A message that says what is missing if this process may not start an app as another user, otherwise null. Checked before
    /// anything is started, so that the reason shows up instead of a failing logon.
    /// </summary>
    public static string? MissingPrivileges()
    {
        var missing = new List<string>();
        foreach (var privilege in new[] { SeTcb, SeAssignPrimaryToken, SeIncreaseQuota })
        {
            if (!EnablePrivilege(privilege)) missing.Add(privilege);
        }
        return missing.Count == 0 ? null :
            $"The server needs the privileges {string.Join(", ", missing)} to start an app as another user. Run it as LocalSystem, " +
            "or grant the account 'Act as part of the operating system' and 'Replace a process level token'.";
    }

    /// <summary>
    /// Starts <paramref name="commandLine"/> as <paramref name="user"/> in <paramref name="workingDirectory"/>.
    /// <paramref name="environment"/> is added to the environment of that user.
    /// </summary>
    public static WindowsLaunch Start(string user, string commandLine, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, Action<string?> onOutput, Action<string?> onError)
    {
        var name = WindowsAccounts.LocalName(user) ?? throw new ArgumentException($"'{user}' is not a local account name.", nameof(user));
        if (MissingPrivileges() is { } missing) throw new InvalidOperationException(missing);
        EnablePrivilege(SeDebug); // to open the process of another user afterwards; LocalSystem and administrators have it

        IntPtr token = IntPtr.Zero, profile = IntPtr.Zero, job = IntPtr.Zero, environmentBlock = IntPtr.Zero;
        IntPtr stdIn = IntPtr.Zero, outRead = IntPtr.Zero, outWrite = IntPtr.Zero, errRead = IntPtr.Zero, errWrite = IntPtr.Zero;
        var pi = new PROCESS_INFORMATION();
        var started = false;
        try
        {
            token = LogonS4U(name);
            profile = LoadProfile(token, name);

            if (!CreateEnvironmentBlock(out environmentBlock, token, false)) throw Last("CreateEnvironmentBlock");
            var block = BuildEnvironmentBlock(ReadEnvironmentBlock(environmentBlock), environment);

            var security = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
            stdIn = CreateFile("NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, ref security, OPEN_EXISTING, 0, IntPtr.Zero);
            if (stdIn == INVALID_HANDLE_VALUE) throw Last("CreateFile(NUL)");
            CreateInheritablePipe(out outRead, out outWrite);
            CreateInheritablePipe(out errRead, out errWrite);

            var startup = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                dwFlags = STARTF_USESTDHANDLES,
                hStdInput = stdIn,
                hStdOutput = outWrite,
                hStdError = errWrite,
            };

            var line = new StringBuilder(commandLine);
            var blockPointer = Marshal.AllocHGlobal(block.Length);
            try
            {
                Marshal.Copy(block, 0, blockPointer, block.Length);
                if (!CreateProcessAsUser(token, null, line, IntPtr.Zero, IntPtr.Zero, true,
                        CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW | CREATE_SUSPENDED, blockPointer, workingDirectory, ref startup, out pi))
                {
                    throw Last($"CreateProcessAsUser as '{name}'");
                }
            }
            finally { Marshal.FreeHGlobal(blockPointer); }
            started = true;

            job = CreateKillOnCloseJob();
            if (!AssignProcessToJobObject(job, pi.hProcess)) throw Last("AssignProcessToJobObject");

            // The app has its own copies of these now.
            CloseHandle(stdIn); stdIn = IntPtr.Zero;
            CloseHandle(outWrite); outWrite = IntPtr.Zero;
            CloseHandle(errWrite); errWrite = IntPtr.Zero;

            Pump(outRead, onOutput); outRead = IntPtr.Zero;
            Pump(errRead, onError); errRead = IntPtr.Zero;

            var process = Process.GetProcessById(pi.dwProcessId);
            var launch = new WindowsLaunch(process, token, profile, job, pi.hThread, pi.hProcess);
            token = profile = job = IntPtr.Zero;
            return launch;
        }
        catch
        {
            if (started) TerminateProcess(pi.hProcess, 1);
            if (started) { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
            if (job != IntPtr.Zero) CloseHandle(job);
            if (profile != IntPtr.Zero && token != IntPtr.Zero) UnloadUserProfile(token, profile);
            if (token != IntPtr.Zero) CloseHandle(token);
            throw;
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero) DestroyEnvironmentBlock(environmentBlock);
            foreach (var handle in new[] { stdIn, outRead, outWrite, errRead, errWrite })
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// The exit code of the app, or null while it runs. <see cref="System.Diagnostics.Process.ExitCode"/> cannot be used: the process was not
    /// started by that object, so it throws.
    /// </summary>
    public int? ExitCode => processHandle != IntPtr.Zero && GetExitCodeProcess(processHandle, out var code) && code != STILL_ACTIVE ? unchecked((int)code) : null;

    /// <summary>Lets the suspended app run.</summary>
    public void Resume()
    {
        if (thread == IntPtr.Zero) return;
        if (ResumeThread(thread) == -1) throw Last("ResumeThread");
        CloseHandle(thread);
        thread = IntPtr.Zero;
    }

    /// <summary>Ends the app if it still runs (closing the job does that) and releases the logon.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (thread != IntPtr.Zero) CloseHandle(thread);
        if (job != IntPtr.Zero) CloseHandle(job);
        if (profile != IntPtr.Zero) UnloadUserProfile(token, profile);
        if (token != IntPtr.Zero) CloseHandle(token);
        if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
        processHandle = IntPtr.Zero;
    }

    // --- S4U logon ---

    static IntPtr LogonS4U(string name)
    {
        var processName = AnsiString("AspNetCoreSharedServer");
        var status = LsaRegisterLogonProcess(ref processName, out var lsa, out _);
        FreeAnsi(processName);
        if (status != 0) throw Lsa(status, "LsaRegisterLogonProcess (the privilege 'Act as part of the operating system' is needed)");

        var origin = AnsiString("AspNetCoreSharedServer");
        var package = AnsiString("MICROSOFT_AUTHENTICATION_PACKAGE_V1_0");
        var buffer = IntPtr.Zero;
        try
        {
            status = LsaLookupAuthenticationPackage(lsa, ref package, out var packageId);
            if (status != 0) throw Lsa(status, "LsaLookupAuthenticationPackage");

            // MSV1_0_S4U_LOGON followed by the two strings it points to, all in one block.
            var domain = Environment.MachineName;
            var headerSize = Marshal.SizeOf<MSV1_0_S4U_LOGON>();
            var userBytes = name.Length * 2;
            var domainBytes = domain.Length * 2;
            buffer = Marshal.AllocHGlobal(headerSize + userBytes + domainBytes);
            var userPointer = buffer + headerSize;
            var domainPointer = userPointer + userBytes;
            Marshal.Copy(name.ToCharArray(), 0, userPointer, name.Length);
            Marshal.Copy(domain.ToCharArray(), 0, domainPointer, domain.Length);
            Marshal.StructureToPtr(new MSV1_0_S4U_LOGON
            {
                MessageType = MsV1_0S4ULogon,
                Flags = 0,
                UserPrincipalName = new LSA_UNICODE_STRING { Length = (ushort)userBytes, MaximumLength = (ushort)userBytes, Buffer = userPointer },
                DomainName = new LSA_UNICODE_STRING { Length = (ushort)domainBytes, MaximumLength = (ushort)domainBytes, Buffer = domainPointer },
            }, buffer, false);

            var source = new TOKEN_SOURCE { SourceName = Encoding.ASCII.GetBytes("ASPSRV\0\0") };
            AllocateLocallyUniqueId(out source.SourceIdentifier);

            // A batch logon is what Task Scheduler uses for S4U; it needs the right 'Log on as a batch job', which EnsureUser grants.
            status = LsaLogonUser(lsa, ref origin, SECURITY_LOGON_TYPE_BATCH, packageId, buffer, (uint)(headerSize + userBytes + domainBytes),
                IntPtr.Zero, ref source, out var profileBuffer, out _, out _, out var logonToken, out _, out var subStatus);
            if (profileBuffer != IntPtr.Zero) LsaFreeReturnBuffer(profileBuffer);
            if (status != 0) throw Lsa(status == STATUS_ACCOUNT_RESTRICTION ? (uint)subStatus : status, $"The S4U logon of '{name}' failed (does the user exist, and may it log on as a batch job?)");

            try
            {
                // A primary token is needed for CreateProcessAsUser.
                if (!DuplicateTokenEx(logonToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
                    throw Last("DuplicateTokenEx");
                return primary;
            }
            finally { CloseHandle(logonToken); }
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            FreeAnsi(origin);
            FreeAnsi(package);
            LsaDeregisterLogonProcess(lsa);
        }
    }

    static IntPtr LoadProfile(IntPtr token, string name)
    {
        var info = new PROFILEINFO { dwSize = Marshal.SizeOf<PROFILEINFO>(), dwFlags = PI_NOUI, lpUserName = name };
        if (!LoadUserProfile(token, ref info)) throw Last($"LoadUserProfile of '{name}'");
        return info.hProfile;
    }

    // --- environment ---

    /// <summary>The variables of an environment block: a series of NAME=value strings that ends with an empty one.</summary>
    static Dictionary<string, string> ReadEnvironmentBlock(IntPtr block)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var pointer = block; ;)
        {
            var entry = Marshal.PtrToStringUni(pointer) ?? "";
            if (entry.Length == 0) break;
            var equals = entry.IndexOf('=', 1); // a name can start with '=', like the "=C:" entries
            if (equals > 0) variables[entry.Substring(0, equals)] = entry.Substring(equals + 1);
            pointer += (entry.Length + 1) * 2;
        }
        return variables;
    }

    /// <summary>
    /// The environment block for the app: the environment of its user, then the variables of the server on top, so that an app
    /// cannot get a different <c>ASPNET_SERVER</c> or <c>PROXY_PROTOCOL</c> than the server defines.
    /// </summary>
    internal static byte[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string> userEnvironment, IReadOnlyDictionary<string, string> overrides)
    {
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in userEnvironment) merged[variable.Key] = variable.Value;
        foreach (var variable in overrides) merged[variable.Key] = variable.Value;

        var text = new StringBuilder();
        foreach (var variable in merged) text.Append(variable.Key).Append('=').Append(variable.Value).Append('\0');
        text.Append('\0');
        return Encoding.Unicode.GetBytes(text.ToString());
    }

    // --- pipes, job ---

    static void CreateInheritablePipe(out IntPtr read, out IntPtr write)
    {
        var security = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!CreatePipe(out read, out write, ref security, 0)) throw Last("CreatePipe");
        // Only the end that the app gets is inherited.
        if (!SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0)) throw Last("SetHandleInformation");
    }

    /// <summary>Reads lines from the pipe in the background and ends with a null line, like <see cref="Process.OutputDataReceived"/>.</summary>
    static void Pump(IntPtr readHandle, Action<string?> onLine)
    {
        var stream = new FileStream(new SafeFileHandle(readHandle, true), FileAccess.Read, 4096, false);
        _ = Task.Run(() =>
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null) onLine(line);
            }
            catch (IOException) { }
            finally { onLine(null); }
        });
    }

    static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw Last("CreateJobObject");
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, pointer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, pointer, (uint)size))
            {
                CloseHandle(job);
                throw Last("SetInformationJobObject");
            }
        }
        finally { Marshal.FreeHGlobal(pointer); }
        return job;
    }

    // --- privileges ---

    const string SeTcb = "SeTcbPrivilege", SeAssignPrimaryToken = "SeAssignPrimaryTokenPrivilege",
        SeIncreaseQuota = "SeIncreaseQuotaPrivilege", SeDebug = "SeDebugPrivilege";

    /// <summary>Enables a privilege that this process holds. False if it does not hold it.</summary>
    static bool EnablePrivilege(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var processToken)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid)) return false;
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(processToken, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
        }
        finally { CloseHandle(processToken); }
    }

    // --- errors, strings ---

    static Win32Exception Last(string what) => new(Marshal.GetLastWin32Error(), $"{what} failed.");

    static Win32Exception Lsa(uint status, string what) => new(LsaNtStatusToWinError(status), $"{what} failed (status 0x{status:X8}).");

    static LSA_STRING AnsiString(string value) => new()
    {
        Length = (ushort)value.Length,
        MaximumLength = (ushort)(value.Length + 1),
        Buffer = Marshal.StringToHGlobalAnsi(value),
    };

    static void FreeAnsi(LSA_STRING value) => Marshal.FreeHGlobal(value.Buffer);

    // --- native declarations ---

    const uint MsV1_0S4ULogon = 12;
    const int SECURITY_LOGON_TYPE_BATCH = 4;
    const uint STATUS_ACCOUNT_RESTRICTION = 0xC000006E;
    const uint MAXIMUM_ALLOWED = 0x02000000;
    const int SecurityImpersonation = 2, TokenPrimary = 1;
    const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 2;
    const int ERROR_NOT_ALL_ASSIGNED = 1300;
    const uint CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;
    const int STARTF_USESTDHANDLES = 0x100;
    const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, HANDLE_FLAG_INHERIT = 1;
    const int PI_NOUI = 1;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    const int JobObjectExtendedLimitInformation = 9;
    const uint STILL_ACTIVE = 259;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct LSA_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSV1_0_S4U_LOGON
    {
        public uint MessageType;
        public uint Flags;
        public LSA_UNICODE_STRING UserPrincipalName;
        public LSA_UNICODE_STRING DomainName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_SOURCE
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] SourceName;
        public LUID SourceIdentifier;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct QUOTA_LIMITS
    {
        public IntPtr PagedPoolLimit, NonPagedPoolLimit, MinimumWorkingSetSize, MaximumWorkingSetSize, PagefileLimit;
        public long TimeLimit;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROFILEINFO
    {
        public int dwSize, dwFlags;
        public string lpUserName;
        public string? lpProfilePath, lpDefaultPath, lpServerName, lpPolicyPath;
        public IntPtr hProfile;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("secur32.dll")]
    static extern uint LsaRegisterLogonProcess(ref LSA_STRING logonProcessName, out IntPtr lsaHandle, out uint securityMode);

    [DllImport("secur32.dll")]
    static extern uint LsaLookupAuthenticationPackage(IntPtr lsaHandle, ref LSA_STRING packageName, out uint authenticationPackage);

    [DllImport("secur32.dll")]
    static extern uint LsaLogonUser(IntPtr lsaHandle, ref LSA_STRING originName, int logonType, uint authenticationPackage,
        IntPtr authenticationInformation, uint authenticationInformationLength, IntPtr localGroups, ref TOKEN_SOURCE sourceContext,
        out IntPtr profileBuffer, out uint profileBufferLength, out LUID logonId, out IntPtr token, out QUOTA_LIMITS quotas, out int subStatus);

    [DllImport("secur32.dll")]
    static extern uint LsaFreeReturnBuffer(IntPtr buffer);

    [DllImport("secur32.dll")]
    static extern uint LsaDeregisterLogonProcess(IntPtr lsaHandle);

    [DllImport("advapi32.dll")]
    static extern int LsaNtStatusToWinError(uint status);

    [DllImport("advapi32.dll")]
    static extern bool AllocateLocallyUniqueId(out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("userenv.dll", EntryPoint = "LoadUserProfileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LoadUserProfile(IntPtr token, ref PROFILEINFO profileInfo);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool UnloadUserProfile(IntPtr token, IntPtr profile);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SECURITY_ATTRIBUTES attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, ref SECURITY_ATTRIBUTES attributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
