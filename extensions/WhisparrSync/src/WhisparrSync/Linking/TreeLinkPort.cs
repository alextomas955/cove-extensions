using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WhisparrSync.Import;

namespace WhisparrSync.Linking;

// Interop on both platforms, because the runtime offers neither act: System.IO can create a
// symbolic link and not a hard one, and it reports no file identity at all. A symbolic link is not
// a substitute; it points at a name, so it dangles the moment the reader renames the file.
internal sealed partial class TreeLinkPort : ITreeLinkPort
{
    // The host's scan honours this file in any directory and applies it to everything below.
    private const string IgnoreFileName = ".coveignore";
    private const string IgnoreEverything = "*\n";

    // errno values, which are the same numbers on every platform this runs on.
    private const int Eexist = 17;
    private const int Enoent = 2;
    private const int Exdev = 18;

    // Win32 error codes.
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorNotSameDevice = 17;
    private const int ErrorAlreadyExists = 183;

    // struct stat as the C library on Linux lays it out. The volume and the file number lead on
    // every architecture; the number of names and the file mode swap places between them, and the
    // name count narrows to four bytes where it follows the mode. Reading a wrong name count is
    // what would let a removal take a file's last name, so the architecture decides the offset
    // rather than one layout being assumed.
    private const int StatBufferBytes = 256;
    private const int VolumeOffset = 0;
    private const int NumberOffset = 8;
    private const int NameCountOffset = 16;
    private const int NarrowNameCountOffset = 20;
    private const int ChangedSecondsOffset = 88;
    private const int ChangedNanosecondsOffset = 96;

    public ProbedLink? Identify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return OperatingSystem.IsWindows() ? IdentifyOnWindows(path) : IdentifyOnUnix(path);
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            return null;
        }
    }

    public LinkOutcome Link(string existingPath, string newPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);

        try
        {
            return OperatingSystem.IsWindows()
                ? LinkOnWindows(existingPath, newPath)
                : LinkOnUnix(existingPath, newPath);
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            return LinkOutcome.Refused;
        }
    }

    public bool EnsureFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            Directory.CreateDirectory(path);
            return Directory.Exists(path);
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            return false;
        }
    }

    public IEnumerable<string> NamesIn(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return Streamed(folder);
    }

    public NameRemoval Remove(string treeRoot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (PathCandidateGuard.TailBelow(path, treeRoot) is null)
        {
            return NameRemoval.Refused;
        }

        try
        {
            if (!File.Exists(path))
            {
                return NameRemoval.NotThere;
            }

            File.Delete(path);
            return NameRemoval.Removed;
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            return NameRemoval.Refused;
        }
    }

    public bool WriteIgnore(string treeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);

        if (PathCandidateGuard.CandidateUnder(treeRoot, IgnoreFileName) is not { } ignoreFile)
        {
            return false;
        }

        try
        {
            if (File.Exists(ignoreFile)
                && string.Equals(File.ReadAllText(ignoreFile), IgnoreEverything, StringComparison.Ordinal))
            {
                return true;
            }

            Directory.CreateDirectory(treeRoot);
            File.WriteAllText(ignoreFile, IgnoreEverything);
            return File.Exists(ignoreFile);
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            return false;
        }
    }

    // A folder that cannot be read raises at the call that opens the walk, and one that goes away
    // part way through raises on a later step, so both are caught. Ending the stream answers what
    // an absent folder answers, which is what every caller here acts on.
    private static IEnumerable<string> Streamed(string folder)
    {
        var walk = Opened(folder);
        if (walk is null)
        {
            yield break;
        }

        using (walk)
        {
            while (Advanced(walk) is { } entry)
            {
                yield return Path.GetFileName(entry);
            }
        }
    }

    private static IEnumerator<string>? Opened(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            return null;
        }
    }

    private static string? Advanced(IEnumerator<string> walk)
    {
        try
        {
            return walk.MoveNext() ? walk.Current : null;
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            return null;
        }
    }

    private static bool Unreadable(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or DllNotFoundException
            or EntryPointNotFoundException;

    private static ProbedLink? IdentifyOnUnix(string path)
    {
        var buffer = new byte[StatBufferBytes];
        if (Stat(path, buffer) != 0)
        {
            return null;
        }

        var names = RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86
            ? BitConverter.ToUInt64(buffer, NameCountOffset)
            : BitConverter.ToUInt32(buffer, NarrowNameCountOffset);

        var changed = DateTimeOffset
            .FromUnixTimeSeconds(BitConverter.ToInt64(buffer, ChangedSecondsOffset))
            .AddTicks(BitConverter.ToInt64(buffer, ChangedNanosecondsOffset) / 100);

        return new ProbedLink(
            new FileIdentity(
                BitConverter.ToUInt64(buffer, VolumeOffset),
                BitConverter.ToUInt64(buffer, NumberOffset)),
            (int)names,
            changed);
    }

    private static LinkOutcome LinkOnUnix(string existingPath, string newPath)
    {
        if (LinkAt(existingPath, newPath) == 0)
        {
            return LinkOutcome.Linked;
        }

        return Marshal.GetLastPInvokeError() switch
        {
            Eexist => LinkOutcome.NameAlreadyThere,

            // Also what a missing folder above the new name answers. Both are a path the caller
            // composed that is not on disk, and the caller makes the folder before it links.
            Enoent => LinkOutcome.SourceNotThere,
            Exdev => LinkOutcome.OnAnotherDevice,
            _ => LinkOutcome.Refused,
        };
    }

    private static ProbedLink? IdentifyOnWindows(string path)
    {
        using var handle = File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (!GetFileInformationByHandle(handle, out var information))
        {
            return null;
        }

        return new ProbedLink(
            new FileIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow),
            (int)information.NumberOfLinks,
            DateTimeOffset.FromFileTime(
                ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow));
    }

    private static LinkOutcome LinkOnWindows(string existingPath, string newPath)
    {
        if (CreateHardLink(newPath, existingPath, IntPtr.Zero))
        {
            return LinkOutcome.Linked;
        }

        return Marshal.GetLastPInvokeError() switch
        {
            ErrorAlreadyExists => LinkOutcome.NameAlreadyThere,
            ErrorFileNotFound or ErrorPathNotFound => LinkOutcome.SourceNotThere,
            ErrorNotSameDevice => LinkOutcome.OnAnotherDevice,
            _ => LinkOutcome.Refused,
        };
    }

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinkAt(string existingPath, string newPath);

    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Stat(string path, byte[] buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(
        string newPath, string existingPath, IntPtr securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle file, out FileInformation information);

    // BY_HANDLE_FILE_INFORMATION. Every member is four bytes wide, including each half of the three
    // times, so the pairs are spelled out rather than declared as one wider field: a wider field
    // would take a wider alignment and move everything after it.
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
