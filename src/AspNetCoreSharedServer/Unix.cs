using Mono.Unix;
using System;
using System.Collections.Generic;
using System.Text;

namespace AspNetCoreSharedServer;

public class Unix
{
    public static void GrantUnixPermissions(string path, FileAccessPermissions mode, bool resetChildPermissions = false)
    {
        if (!resetChildPermissions)
        {
            var info = UnixFileSystemInfo.GetFileSystemEntry(path);
            if (info != null && info.Exists)
            {
                info.FileAccessPermissions = mode;
                info.Refresh();
            }
            else throw new FileNotFoundException(path);
        }
        else
        {
            GrantUnixPermissions(path, mode, false);

            foreach (var e in Directory.EnumerateFileSystemEntries(path))
            {
                var info = UnixFileSystemInfo.GetFileSystemEntry(e);
                if (info != null && info.Exists)
                {
                    info.FileAccessPermissions = mode;
                    info.Refresh();
                }
                else throw new FileNotFoundException(e);
            }
        }
    }
}
