using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;

namespace AspNetCoreSharedServer;

/// <summary>
/// Local Windows users and groups: the counterpart of <see cref="Unix"/>'s <c>UserExists</c>, <c>GroupExists</c> and
/// <c>IsUserInGroup</c>, plus creating the local account that an app runs as. The account gets a random password that nobody
/// knows: <see cref="WindowsLaunch"/> logs it on with S4U, which needs no password.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAccounts
{
    const int NERR_Success = 0;
    const uint UF_SCRIPT = 0x0001, UF_PASSWD_CANT_CHANGE = 0x0040, UF_DONT_EXPIRE_PASSWD = 0x10000;
    const uint USER_PRIV_USER = 1;
    const uint LG_INCLUDE_INDIRECT = 1;
    const int MAX_PREFERRED_LENGTH = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct USER_INFO_1
    {
        public string usri1_name;
        public string usri1_password;
        public uint usri1_password_age;
        public uint usri1_priv;
        public string? usri1_home_dir;
        public string? usri1_comment;
        public uint usri1_flags;
        public string? usri1_script_path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct LOCALGROUP_USERS_INFO_0
    {
        public string lgrui0_name;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public int Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserGetInfo(string? server, string username, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetLocalGroupGetInfo(string? server, string groupname, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserGetLocalGroups(string? server, string username, int level, uint flags, out IntPtr buffer,
        int prefmaxlen, out int entriesread, out int totalentries);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserAdd(string? server, int level, ref USER_INFO_1 buffer, out int parmError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserDel(string? server, string username);

    [DllImport("netapi32.dll")]
    static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("advapi32.dll")]
    static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES attributes, uint access, out IntPtr policy);

    [DllImport("advapi32.dll")]
    static extern uint LsaAddAccountRights(IntPtr policy, byte[] accountSid, LSA_UNICODE_STRING[] userRights, uint countOfRights);

    [DllImport("advapi32.dll")]
    static extern uint LsaClose(IntPtr policy);

    [DllImport("advapi32.dll")]
    static extern int LsaNtStatusToWinError(uint status);

    /// <summary>
    /// The user name without a machine prefix: <c>name</c>, <c>.\name</c> and <c>MACHINE\name</c> all name the local account
    /// <c>name</c>. Null if the name belongs to a domain, which this class does not handle.
    /// </summary>
    internal static string? LocalName(string account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        var slash = account.LastIndexOf('\\');
        if (slash < 0) return account;
        var domain = account.Substring(0, slash);
        if (domain == "." || domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return account.Substring(slash + 1);
        return null;
    }

    public static bool UserExists(string user)
    {
        var name = LocalName(user);
        if (name == null) return false;
        if (NetUserGetInfo(null, name, 0, out var buffer) != NERR_Success) return false;
        NetApiBufferFree(buffer);
        return true;
    }

    public static bool GroupExists(string group)
    {
        var name = LocalName(group);
        if (name == null) return false;
        if (NetLocalGroupGetInfo(null, name, 0, out var buffer) != NERR_Success) return false;
        NetApiBufferFree(buffer);
        return true;
    }

    /// <summary>True if the local user is a member of the local group, directly or through another group. False if either is unknown.</summary>
    public static bool IsUserInGroup(string user, string group)
    {
        var userName = LocalName(user);
        var groupName = LocalName(group);
        if (userName == null || groupName == null) return false;
        if (NetUserGetLocalGroups(null, userName, 0, LG_INCLUDE_INDIRECT, out var buffer, MAX_PREFERRED_LENGTH, out var read, out _) != NERR_Success) return false;
        try
        {
            var size = Marshal.SizeOf<LOCALGROUP_USERS_INFO_0>();
            for (var i = 0; i < read; i++)
            {
                var entry = Marshal.PtrToStructure<LOCALGROUP_USERS_INFO_0>(buffer + i * size);
                if (string.Equals(entry.lgrui0_name, groupName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally { NetApiBufferFree(buffer); }
    }

    /// <summary>
    /// Creates the local account <paramref name="user"/> for an app, unless it exists. It gets a random password that is thrown away,
    /// the right to log on as a batch job (what an S4U logon needs) and the denial of every interactive, remote desktop and network
    /// logon, so that nobody can log on as it. Needs administrator rights. Returns true if the account was created.
    /// </summary>
    public static bool EnsureUser(string user, string? comment = null)
    {
        var name = LocalName(user) ?? throw new ArgumentException($"'{user}' is not a local account name.", nameof(user));
        if (UserExists(name)) return false;

        var info = new USER_INFO_1
        {
            usri1_name = name,
            usri1_password = RandomPassword(),
            usri1_priv = USER_PRIV_USER,
            usri1_comment = comment ?? "AspNetCoreSharedServer application account",
            usri1_flags = UF_SCRIPT | UF_PASSWD_CANT_CHANGE | UF_DONT_EXPIRE_PASSWD,
        };
        var status = NetUserAdd(null, 1, ref info, out var parameterError);
        if (status != NERR_Success) throw new Win32Exception(status, $"Could not create user '{name}' (NetUserAdd: {status}, parameter {parameterError}).");

        try
        {
            AddRights(name, "SeBatchLogonRight", "SeDenyInteractiveLogonRight", "SeDenyRemoteInteractiveLogonRight", "SeDenyNetworkLogonRight");
        }
        catch
        {
            // An account without these rights would be left behind: it could log on interactively, and the next call would find it and
            // not give it the rights. Take it away again.
            NetUserDel(null, name);
            throw;
        }
        return true;
    }

    /// <summary>Deletes the local account. Used by the tests; the server never deletes an account.</summary>
    public static void DeleteUser(string user)
    {
        var name = LocalName(user) ?? throw new ArgumentException($"'{user}' is not a local account name.", nameof(user));
        var status = NetUserDel(null, name);
        if (status != NERR_Success) throw new Win32Exception(status, $"Could not delete user '{name}' (NetUserDel: {status}).");
    }

    /// <summary>A uniformly distributed random number from 0 up to, not including, <paramref name="max"/> (GetInt32 is not in netstandard2.0).</summary>
    static int RandomInt(int max)
    {
        using var random = RandomNumberGenerator.Create();
        var bytes = new byte[4];
        var limit = uint.MaxValue - uint.MaxValue % (uint)max; // reject the numbers that would make the low ones more likely
        uint value;
        do
        {
            random.GetBytes(bytes);
            value = BitConverter.ToUInt32(bytes, 0);
        } while (value >= limit);
        return (int)(value % (uint)max);
    }

    /// <summary>A password that satisfies a complexity policy (upper, lower, digit and symbol) and that nobody ever sees.</summary>
    internal static string RandomPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz", digits = "23456789", symbols = "!@#$%^&*()-_=+";
        var all = upper + lower + digits + symbols;
        var chars = new List<char>
        {
            upper[RandomInt(upper.Length)], lower[RandomInt(lower.Length)],
            digits[RandomInt(digits.Length)], symbols[RandomInt(symbols.Length)],
        };
        while (chars.Count < 40) chars.Add(all[RandomInt(all.Length)]);
        // Shuffle, so that the first four are not always one of each kind.
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomInt(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }

    static void AddRights(string account, params string[] rights)
    {
        var sid = new NTAccount(account).Translate(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new InvalidOperationException($"No SID for '{account}'.");
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);

        var attributes = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
        const uint POLICY_CREATE_ACCOUNT = 0x10, POLICY_LOOKUP_NAMES = 0x800;
        var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, POLICY_CREATE_ACCOUNT | POLICY_LOOKUP_NAMES, out var policy);
        if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status), "Could not open the local security policy.");

        var strings = new LSA_UNICODE_STRING[rights.Length];
        try
        {
            for (var i = 0; i < rights.Length; i++)
            {
                strings[i] = new LSA_UNICODE_STRING
                {
                    Buffer = Marshal.StringToHGlobalUni(rights[i]),
                    Length = (ushort)(rights[i].Length * 2),
                    MaximumLength = (ushort)((rights[i].Length + 1) * 2),
                };
            }
            status = LsaAddAccountRights(policy, sidBytes, strings, (uint)strings.Length);
            if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status), $"Could not set the logon rights of '{account}'.");
        }
        finally
        {
            foreach (var s in strings) if (s.Buffer != IntPtr.Zero) Marshal.FreeHGlobal(s.Buffer);
            LsaClose(policy);
        }
    }
}
