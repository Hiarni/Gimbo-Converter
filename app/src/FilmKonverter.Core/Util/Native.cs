using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FilmKonverter.Core.Util;

public static partial class Native
{
    public static bool TryCreateDirectoryExclusive(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (CreateDirectoryW(@"\\?\" + ToExtendedPath(path), IntPtr.Zero)) return true;
            var err = Marshal.GetLastPInvokeError();
            if (err == 183 ) return false;
            throw new IOException(L.T($"Sperre nicht anlegbar ({path}): Windows-Fehler {err}", $"Cannot create lock ({path}): Windows error {err}"),
                new System.ComponentModel.Win32Exception(err));
        }
        if (mkdir(path, 0x1ED ) == 0) return true;
        var errno = Marshal.GetLastPInvokeError();
        if (errno == 17 ) return false;
        throw new IOException(L.T($"Sperre nicht anlegbar ({path}): Fehler {errno}", $"Cannot create lock ({path}): error {errno}"));
    }

    private static string ToExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) && !full.StartsWith(@"\\?\", StringComparison.Ordinal))
            return @"UNC\" + full.Substring(2);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full.Substring(4) : full;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectoryW(string path, IntPtr securityAttributes);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int mkdir(string path, int mode);

    // ---------------- Anhalten / Fortsetzen ----------------

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(IntPtr processHandle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(IntPtr processHandle);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int sig);

    public static bool Suspend(Process p)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return NtSuspendProcess(p.Handle) == 0;
            return kill(p.Id, 19 ) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool Resume(Process p)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return NtResumeProcess(p.Handle) == 0;
            return kill(p.Id, 18 ) == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- Freier Speicher ----------------

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string directory, out ulong freeForUser, out ulong total, out ulong totalFree);

    public static long FreeBytes(string folder)
    {
        try
        {
            var dir = folder;
            while (!Directory.Exists(dir))
            {
                var parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir) return -1;
                dir = parent;
            }
            if (OperatingSystem.IsWindows())
            {
                var d = dir.EndsWith('\\') ? dir : dir + "\\";
                return GetDiskFreeSpaceExW(d, out var free, out _, out _) ? (long)Math.Min(free, long.MaxValue) : -1;
            }
            return new DriveInfo(dir).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    // ---------------- Prioritaet ----------------

    public static void SetPriority(Process p, ProcessPriorityClass priority)
    {
        try
        {
            if (priority != ProcessPriorityClass.Normal) p.PriorityClass = priority;
        }
        catch
        {
        }
    }

    // ---------------- ffmpeg endet mit dem Programm ----------------

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimit
    {
        public JobBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimit info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private static readonly Lazy<IntPtr> KillJob = new(() =>
    {
        try
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JobExtendedLimit { BasicLimitInformation = new JobBasicLimit { LimitFlags = JobObjectLimitKillOnJobClose } };
            return SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JobExtendedLimit>()) ? job : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    });

    public static void KillWithApp(Process p)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var job = KillJob.Value;
            if (job != IntPtr.Zero) AssignProcessToJobObject(job, p.Handle);
        }
        catch
        {
        }
    }
}
