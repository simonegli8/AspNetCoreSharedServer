using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// The owner of a file comes from the stat record of the operating system, whose layout differs between macOS, Linux on x86_64 and
/// Linux on arm64 (<see cref="UnixMac"/>, <see cref="UnixX64"/>, <see cref="UnixArm"/>, and the layout-free <c>statx</c> in
/// <see cref="UnixLinux"/>). Wrong offsets do not crash: they silently read another field, and the ownership checks then trust a wrong
/// owner. The first group of tests builds records byte by byte after the real layouts and runs on every operating system, so the layouts of
/// macOS and arm64 are tested on a Windows or x86_64 machine as well. The second group asks the real operating system.
/// </summary>
public class UnixStatTests
{
    const uint Uid = 501, Gid = 20;       // a typical macOS account: user 501, group "staff"
    const uint Mode = 0x1A4;               // 0644
    const ushort RegularFile = 0x8000;     // S_IFREG, in the file type bits of st_mode

    static byte[] Record(Action<byte[]> fill)
    {
        var record = new byte[512];        // the kernel zero-fills what it does not use
        fill(record);
        return record;
    }

    static void Put(byte[] record, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(record, offset);
    static void Put(byte[] record, int offset, ushort value) => BitConverter.GetBytes(value).CopyTo(record, offset);
    static void Put(byte[] record, int offset, ulong value) => BitConverter.GetBytes(value).CopyTo(record, offset);

    // --- layouts, from the headers (see the remarks of the classes) ---

    /// <summary>macOS, 64-bit inode: dev_t int32@0, mode_t uint16@4, nlink_t uint16@6, ino uint64@8, uid@16, gid@20, rdev@24, atime@32.</summary>
    static byte[] MacRecord() => Record(r =>
    {
        Put(r, 4, (ushort)(RegularFile | Mode));
        Put(r, 6, (ushort)1);
        Put(r, 8, 12345UL);
        Put(r, 16, Uid);
        Put(r, 20, Gid);
        Put(r, 32, 1_780_000_000UL); // st_atimespec.tv_sec: what the Linux layout would take for the gid
    });

    /// <summary>macOS on Intel before the 64-bit inode: dev@0, ino uint32@4, mode@8, nlink@10, uid@12, gid@16, rdev@20, atime@24.</summary>
    static byte[] MacLegacyRecord() => Record(r =>
    {
        Put(r, 4, 12345u);
        Put(r, 8, (ushort)(RegularFile | Mode));
        Put(r, 10, (ushort)1);
        Put(r, 12, Uid);
        Put(r, 16, Gid);
        Put(r, 24, 1_780_000_000UL);
    });

    /// <summary>Linux x86_64: dev@0, ino@8, nlink@16 (8 bytes each), mode@24, uid@28, gid@32.</summary>
    static byte[] X64Record() => Record(r =>
    {
        Put(r, 24, (uint)(RegularFile | Mode));
        Put(r, 28, Uid);
        Put(r, 32, Gid);
    });

    /// <summary>Linux arm64: dev@0, ino@8 (8 bytes each), mode@16, nlink@20, uid@24, gid@28.</summary>
    static byte[] ArmRecord() => Record(r =>
    {
        Put(r, 16, (uint)(RegularFile | Mode));
        Put(r, 20, 1u);
        Put(r, 24, Uid);
        Put(r, 28, Gid);
    });

    /// <summary>Linux 32-bit ARM (EABI struct stat64): dev@0 (8 bytes), padding@8, ino uint32@12, mode@16, nlink@20, uid@24, gid@28.</summary>
    static byte[] Arm32Record() => Record(r =>
    {
        Put(r, 12, 12345u);
        Put(r, 16, (uint)(RegularFile | Mode));
        Put(r, 20, 1u);
        Put(r, 24, Uid);
        Put(r, 28, Gid);
    });

    /// <summary>
    /// Linux s390x: dev@0, ino@8, nlink@16 (8 bytes each), mode@24, uid@28, gid@32. Like all records here it is written in the byte order of
    /// the machine running the test, as the kernel does it: on a big-endian s390x the same test builds a big-endian record.
    /// </summary>
    static byte[] S390xRecord() => Record(r =>
    {
        Put(r, 16, 1UL);
        Put(r, 24, (uint)(RegularFile | Mode));
        Put(r, 28, Uid);
        Put(r, 32, Gid);
    });

    /// <summary>statx on every Linux: mask@0, blksize@4, attributes@8, nlink@16, uid@20, gid@24, mode uint16@28.</summary>
    static byte[] StatxRecord(uint mask = 0x7FF) => Record(r =>
    {
        Put(r, 0, mask);
        Put(r, 16, 1u);
        Put(r, 20, Uid);
        Put(r, 24, Gid);
        Put(r, 28, (ushort)(RegularFile | Mode));
    });

    // --- the layouts read the right fields ---

    [Fact]
    public void UnixMac_reads_the_owner_from_the_64_bit_inode_record()
    {
        Assert.Equal((Uid, Gid), UnixMac.Parse(MacRecord(), Mode));
    }

    [Fact]
    public void UnixX64_reads_the_owner_from_the_x86_64_record()
    {
        Assert.Equal((Uid, Gid), UnixX64.Parse(X64Record(), Mode));
    }

    [Fact]
    public void UnixArm_reads_the_owner_from_the_arm64_record()
    {
        Assert.Equal((Uid, Gid), UnixArm.Parse(ArmRecord(), Mode));
    }

    [Fact]
    public void UnixArm_reads_the_owner_from_the_32_bit_arm_record_too()
    {
        // The inode is 4 bytes wide there and sits at 12, but the fields that are read have the same offsets as on arm64.
        Assert.Equal((Uid, Gid), UnixArm.Parse(Arm32Record(), Mode));
    }

    [Fact]
    public void UnixS390x_reads_the_owner_from_the_s390x_record()
    {
        Assert.Equal((Uid, Gid), UnixS390x.Parse(S390xRecord(), Mode));
    }

    [Fact]
    public void The_architecture_constant_for_s390x_is_the_one_of_the_enum()
    {
        // netstandard2.0 (the Api project) has no Architecture.S390x, so UnixLinux uses the number. It must be the same.
        Assert.Equal(Architecture.S390x, UnixLinux.S390x);
    }

    [Fact]
    public void The_s390x_record_is_refused_by_the_layouts_that_it_does_not_fit()
    {
        Assert.Throws<NotSupportedException>(() => UnixArm.Parse(S390xRecord(), Mode));
        Assert.Throws<NotSupportedException>(() => UnixMac.Parse(S390xRecord(), Mode));
    }

    [Fact]
    public void UnixLinux_reads_the_owner_from_a_statx_record()
    {
        Assert.Equal((Uid, Gid), UnixLinux.ParseStatx(StatxRecord(), Mode));
    }

    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.S390x)]
    public void UnixLinux_picks_the_layout_of_the_architecture(Architecture architecture)
    {
        var record = architecture switch
        {
            Architecture.X64 => X64Record(),
            Architecture.Arm => Arm32Record(),
            Architecture.S390x => S390xRecord(),
            _ => ArmRecord(),
        };

        Assert.Equal((Uid, Gid), UnixLinux.ParseStat(record, architecture, Mode));
    }

    // --- the old bug: what the Linux x86_64 layout reads from the other records ---

    [Fact]
    public void The_x86_64_layout_reads_the_wrong_owner_from_the_other_records_which_is_what_the_check_is_for()
    {
        // Without the check these are the values the code used to work with. On macOS the owner came out as 0, which is root, for
        // every file, and the group as the access time.
        var (macUid, macGid) = (BitConverter.ToUInt32(MacRecord(), 28), BitConverter.ToUInt32(MacRecord(), 32));
        Assert.Equal(0u, macUid);
        Assert.Equal(1_780_000_000u, macGid);
        // On arm64 the owner is read from the group.
        var arm = ArmRecord();
        Assert.Equal((Gid, 0u), (BitConverter.ToUInt32(arm, 28), BitConverter.ToUInt32(arm, 32)));
    }

    // --- a record with another layout than assumed is refused, never believed ---

    [Fact]
    public void UnixMac_refuses_the_legacy_32_bit_inode_record()
    {
        Assert.Throws<NotSupportedException>(() => UnixMac.Parse(MacLegacyRecord(), Mode));
    }

    [Fact]
    public void UnixMac_refuses_a_Linux_record()
    {
        Assert.Throws<NotSupportedException>(() => UnixMac.Parse(X64Record(), Mode));
        Assert.Throws<NotSupportedException>(() => UnixMac.Parse(ArmRecord(), Mode));
    }

    [Fact]
    public void The_x86_64_and_arm64_layouts_refuse_each_others_records()
    {
        Assert.Throws<NotSupportedException>(() => UnixX64.Parse(ArmRecord(), Mode));
        Assert.Throws<NotSupportedException>(() => UnixArm.Parse(X64Record(), Mode));
    }

    [Fact]
    public void The_x86_64_and_arm64_layouts_refuse_a_macOS_record()
    {
        Assert.Throws<NotSupportedException>(() => UnixX64.Parse(MacRecord(), Mode));
        Assert.Throws<NotSupportedException>(() => UnixArm.Parse(MacRecord(), Mode));
    }

    [Fact]
    public void The_32_bit_arm_record_is_refused_by_the_layouts_that_it_does_not_fit()
    {
        Assert.Throws<NotSupportedException>(() => UnixX64.Parse(Arm32Record(), Mode));
        Assert.Throws<NotSupportedException>(() => UnixMac.Parse(Arm32Record(), Mode));
    }

    [Fact]
    public void A_record_whose_mode_is_not_the_one_the_runtime_reported_is_refused()
    {
        Assert.Throws<NotSupportedException>(() => UnixMac.Parse(MacRecord(), 0x180));            // 0600, but the record says 0644
        Assert.Throws<NotSupportedException>(() => UnixLinux.ParseStatx(StatxRecord(), 0x180));
    }

    [Fact]
    public void Only_the_permission_bits_are_compared_not_the_file_type()
    {
        // The record says "regular file, 0644"; the runtime only reports the permissions.
        Assert.Equal((Uid, Gid), UnixMac.Parse(MacRecord(), Mode));
        // ... but sticky, setuid and setgid are part of the permissions: 01644 is not 0644.
        Assert.Throws<NotSupportedException>(() => UnixMac.Parse(MacRecord(), Mode | 0x200));
    }

    [Theory]
    [InlineData(0x1A)]   // mode, uid and gid are all there: fine below
    public void UnixLinux_accepts_a_statx_record_that_has_the_mode_uid_and_gid(uint mask)
    {
        Assert.Equal((Uid, Gid), UnixLinux.ParseStatx(StatxRecord(mask), Mode));
    }

    [Theory]
    [InlineData(0x12u)]  // no uid
    [InlineData(0x0Au)]  // no gid
    [InlineData(0x18u)]  // no mode
    [InlineData(0u)]
    public void UnixLinux_refuses_a_statx_record_that_does_not_say_who_owns_the_file(uint mask)
    {
        Assert.Throws<NotSupportedException>(() => UnixLinux.ParseStatx(StatxRecord(mask), Mode));
    }

    [Fact]
    public void An_architecture_without_a_known_stat_layout_is_refused_instead_of_guessed()
    {
        Assert.Throws<NotSupportedException>(() => UnixLinux.ParseStat(X64Record(), Architecture.Wasm, Mode));
        Assert.Throws<NotSupportedException>(() => UnixLinux.ParseStat(X64Record(), Architecture.X86, Mode));
    }

    [Fact]
    public void The_permission_bits_of_UnixFileMode_are_the_bits_of_st_mode()
    {
        // The check compares st_mode with UnixFileMode, so the two must use the same bits.
        Assert.Equal(0x1A4, (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead));
        Assert.Equal(0xFFF, (int)(UnixFileMode.All | UnixFileMode.StickyBit | UnixFileMode.SetGroup | UnixFileMode.SetUser));
    }

    // --- the real operating system ---

    static string Capture(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    static void RequireLinuxOrMac() =>
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix.GetOwnerAndGroup is only used on Linux and macOS.");

    /// <summary>"owner group" as stat(1) prints it, which has other options on macOS (BSD) than on Linux (GNU).</summary>
    static string Expected(string path) =>
        OperatingSystem.IsMacOS() ? Capture("stat", "-f", "%Su %Sg", path) : Capture("stat", "-c", "%U %G", path);

    /// <summary>Files with different owners: one of the current user, system files of root, and (as root) one of another user.</summary>
    static IEnumerable<string> FilesToCheck(List<string> created)
    {
        var own = Path.GetTempFileName();
        created.Add(own);
        yield return own;
        yield return "/etc/hosts";
        yield return "/";
        if (Unix.IsRoot && Unix.UserExists("nobody"))
        {
            var other = Path.GetTempFileName();
            created.Add(other);
            Capture("chown", "nobody", other);
            yield return other;
        }
    }

    [SkippableFact]
    public void GetOwnerAndGroup_agrees_with_stat_1()
    {
        RequireLinuxOrMac();
        var created = new List<string>();
        try
        {
            foreach (var path in FilesToCheck(created))
            {
                var (owner, group) = Unix.GetOwnerAndGroup(path);

                Assert.Equal(Expected(path), $"{owner} {group}");
            }
        }
        finally
        {
            created.ForEach(File.Delete);
        }
    }

    [SkippableFact]
    public void Both_Linux_ways_to_ask_agree_with_stat_1()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "These are the two Linux calls; macOS has its own.");
        var created = new List<string>();
        try
        {
            foreach (var path in FilesToCheck(created))
            {
                var mode = (uint)Unix.GetFilePermissions(path);
                var expected = Capture("stat", "-c", "%u %g", path);

                // statx is what a normal system uses. stat is what an older musl (Alpine) falls back to: the layout of this machine.
                try
                {
                    var statx = UnixLinux.GetIdsWithStatx(path, mode);
                    Assert.Equal(expected, $"{statx.Uid} {statx.Gid}");
                }
                catch (EntryPointNotFoundException)
                {
                    // this libc has no statx
                }
                try
                {
                    var stat = UnixLinux.GetIdsWithStat(path, mode);
                    Assert.Equal(expected, $"{stat.Uid} {stat.Gid}");
                }
                catch (EntryPointNotFoundException)
                {
                    // glibc before 2.33 has no stat symbol; statx is used there
                }
            }
        }
        finally
        {
            created.ForEach(File.Delete);
        }
    }

    [SkippableFact]
    public void A_missing_file_is_reported_not_guessed()
    {
        RequireLinuxOrMac();

        Assert.ThrowsAny<IOException>(() => Unix.GetOwnerAndGroup("/no/such/file/xyz"));
    }
}
