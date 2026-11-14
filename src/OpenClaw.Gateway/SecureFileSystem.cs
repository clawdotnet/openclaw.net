using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Gateway;

/// <summary>Handle-bound file operations for paths crossing an authorization boundary.</summary>
internal static partial class SecureFileSystem
{
    private const uint GenericWrite = 0x40000000;
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileAttributeTagInfoClass = 9;

    internal static FileStream OpenForWriteUnderRoot(string root, string path, int bufferSize = 65536)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        if (!Endpoints.WorkspacePathHelpers.IsInsideDirectory(fullPath, fullRoot))
            throw new UnauthorizedAccessException("The destination is outside the trusted root.");

        var relative = Path.GetRelativePath(fullRoot, fullPath);
        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 || components.Any(static component => component is "." or ".."))
            throw new UnauthorizedAccessException("The destination is not a valid file below the trusted root.");

        Directory.CreateDirectory(fullRoot);
        return OperatingSystem.IsWindows()
            ? OpenWindowsForWrite(fullRoot, components, bufferSize)
            : OpenUnixForWrite(fullRoot, components, bufferSize);
    }

    internal static FileStream OpenForReadUnderRoot(string root, string path, int bufferSize = 65536)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        if (!Endpoints.WorkspacePathHelpers.IsInsideDirectory(fullPath, fullRoot))
            throw new UnauthorizedAccessException("The source is outside the trusted root.");

        var relative = Path.GetRelativePath(fullRoot, fullPath);
        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 || components.Any(static component => component is "." or ".."))
            throw new UnauthorizedAccessException("The source is not a valid file below the trusted root.");

        return OperatingSystem.IsWindows()
            ? OpenWindowsForRead(fullRoot, components, bufferSize)
            : OpenUnixForRead(fullRoot, components, bufferSize);
    }

    private static FileStream OpenUnixForWrite(string root, string[] components, int bufferSize)
    {
        var directoryFlags = UnixDirectoryFlags();
        var descriptors = new List<int>();
        try
        {
            var current = Open(root, directoryFlags);
            if (current < 0)
                ThrowLastError($"Unable to open trusted directory '{root}'.");
            descriptors.Add(current);

            foreach (var component in components[..^1])
            {
                if (MkdirAt(current, component, 0x1C0) != 0 && Marshal.GetLastWin32Error() != 17)
                    ThrowLastError($"Unable to create destination directory '{component}'.");
                var child = OpenAt(current, component, directoryFlags | UnixNoFollow());
                if (child < 0)
                    throw new UnauthorizedAccessException($"Destination directory '{component}' is not a trusted directory.");
                descriptors.Add(child);
                current = child;
            }

            var file = OpenOrCreateRegularFileAt(
                current,
                components[^1],
                UnixWriteOnly() | UnixNoFollow() | UnixCloseOnExec());
            if (file < 0)
                throw new UnauthorizedAccessException($"Destination file '{components[^1]}' could not be opened without following links.");
            if (LockExclusive(file) != 0)
            {
                Close(file);
                ThrowLastError($"Unable to lock destination file '{components[^1]}'.");
            }
            var handle = new SafeFileHandle((nint)file, ownsHandle: true);
            try
            {
                var stream = new FileStream(handle, FileAccess.Write, bufferSize, isAsync: false);
                stream.SetLength(0);
                return stream;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var descriptor in descriptors)
                Close(descriptor);
        }
    }

    private static FileStream OpenWindowsForWrite(string root, string[] components, int bufferSize)
    {
        var directoryHandles = new List<SafeFileHandle>();
        try
        {
            var current = root;
            foreach (var component in components[..^1])
            {
                current = Path.Join(current, component);
                Directory.CreateDirectory(current);
            }

            directoryHandles.Add(OpenWindowsDirectory(root, allowTrustedRootReparsePoint: true));
            current = root;
            foreach (var component in components[..^1])
            {
                current = Path.Join(current, component);
                directoryHandles.Add(OpenWindowsDirectory(current, allowTrustedRootReparsePoint: false));
            }

            var filePath = Path.Join(current, components[^1]);
            var handle = CreateFile(
                filePath,
                GenericWrite | FileReadAttributes,
                0,
                0,
                OpenAlways,
                FileAttributeNormal | FileFlagOpenReparsePoint,
                0);
            if (handle.IsInvalid)
                ThrowLastError($"Unable to open destination file '{filePath}'.");
            try
            {
                ThrowIfWindowsReparsePoint(handle, filePath);
                var stream = new FileStream(handle, FileAccess.Write, bufferSize, isAsync: false);
                stream.SetLength(0);
                return stream;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var handle in directoryHandles)
                handle.Dispose();
        }
    }

    private static FileStream OpenUnixForRead(string root, string[] components, int bufferSize)
    {
        var directoryFlags = UnixDirectoryFlags();
        var descriptors = new List<int>();
        try
        {
            var current = Open(root, directoryFlags);
            if (current < 0)
                ThrowLastError($"Unable to open trusted directory '{root}'.");
            descriptors.Add(current);

            foreach (var component in components[..^1])
            {
                var child = OpenAt(current, component, directoryFlags | UnixNoFollow());
                if (child < 0)
                    throw new UnauthorizedAccessException($"Source directory '{component}' is not a trusted directory.");
                descriptors.Add(child);
                current = child;
            }

            var file = OpenAt(current, components[^1], UnixReadOnly() | UnixNoFollow() | UnixCloseOnExec());
            if (file < 0)
                throw new UnauthorizedAccessException($"Source file '{components[^1]}' could not be opened without following links.");
            var handle = new SafeFileHandle((nint)file, ownsHandle: true);
            return new FileStream(handle, FileAccess.Read, bufferSize, isAsync: false);
        }
        finally
        {
            foreach (var descriptor in descriptors)
                Close(descriptor);
        }
    }

    private static FileStream OpenWindowsForRead(string root, string[] components, int bufferSize)
    {
        var directoryHandles = new List<SafeFileHandle>();
        try
        {
            directoryHandles.Add(OpenWindowsDirectory(root, allowTrustedRootReparsePoint: true));
            var current = root;
            foreach (var component in components[..^1])
            {
                current = Path.Join(current, component);
                directoryHandles.Add(OpenWindowsDirectory(current, allowTrustedRootReparsePoint: false));
            }

            var filePath = Path.Join(current, components[^1]);
            var handle = CreateFile(
                filePath,
                GenericRead | FileReadAttributes,
                FileShareRead,
                0,
                OpenExisting,
                FileAttributeNormal | FileFlagOpenReparsePoint,
                0);
            if (handle.IsInvalid)
                ThrowLastError($"Unable to open source file '{filePath}'.");
            try
            {
                ThrowIfWindowsReparsePoint(handle, filePath);
                return new FileStream(handle, FileAccess.Read, bufferSize, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var handle in directoryHandles)
                handle.Dispose();
        }
    }

    private static SafeFileHandle OpenWindowsDirectory(string path, bool allowTrustedRootReparsePoint)
    {
        var handle = CreateFile(
            path,
            FileReadAttributes,
            // Keep every checked ancestor immutable until the final file is open.
            // Denying write and delete sharing blocks reparse mutation and replacement.
            FileShareRead,
            0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
            ThrowLastError($"Unable to open destination directory '{path}'.");
        try
        {
            if (!allowTrustedRootReparsePoint)
                ThrowIfWindowsReparsePoint(handle, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void ThrowIfWindowsReparsePoint(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            ThrowLastError($"Unable to inspect '{path}'.");
        if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
            throw new UnauthorizedAccessException($"Path '{path}' contains a reparse point.");
    }

    private static int UnixDirectoryFlags()
        => UnixDirectory() | UnixCloseOnExec();

    private static int UnixReadOnly() => 0;
    private static int UnixWriteOnly() => 1;
    private static int UnixCreate() => OperatingSystem.IsMacOS() ? 0x200 : 0x40;
    private static int UnixNoFollow() => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
    private static int UnixDirectory() => OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;
    private static int UnixCloseOnExec() => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    private static void ThrowLastError(string message)
        => throw new IOException(message, new Win32Exception(Marshal.GetLastWin32Error()));

    private static int OpenOrCreateRegularFileAt(int directory, string path, int flags)
        => OperatingSystem.IsMacOS()
            ? OpenAtDarwin(directory, path, flags | UnixCreate(), 0x180)
            : OpenAtWithMode(directory, path, flags | UnixCreate(), 0x180);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(int directory, string path, int flags);

    // Darwin ARM64 passes variadic arguments on the stack. Call the fixed-signature
    // libc implementation rather than the public variadic openat wrapper when O_CREAT is set.
    [LibraryImport("libc", EntryPoint = "__openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtDarwin(int directory, string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtWithMode(int directory, string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int LockFile(int descriptor, int operation);

    private static int LockExclusive(int descriptor) => LockFile(descriptor, 2);

    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MkdirAt(int directory, string path, uint mode);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle handle,
        int fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

}
