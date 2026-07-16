using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PackagePilot.Windows.Services;

/// <summary>
/// Prevents an elevated helper from becoming a confused deputy. Before HMAC authentication, the
/// client proves that the named-pipe server is the packaged Package Pilot executable in the same
/// protected install directory and that the server has package identity.
/// </summary>
public static class ElevatedPipeServerVerifier
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private const int MaximumImagePathCharacters = 32768;
    private const uint ProcessorArchitectureUnknown = 0xFFFF;
    internal const string ExpectedPackageName = "PackagePilot.Desktop";
    internal const string ExpectedPackagePublisher = "CN=PackagePilot.Dev";

    /// <summary>
    /// Returns whether the current process is the packaged Package Pilot app in its registered,
    /// protected install directory and the supplied helper is the helper shipped beside it.
    /// Loose, sparse, external-location, and otherwise unverifiable hosts fail closed.
    /// </summary>
    public static bool IsCurrentHostEligibleForPackageAdmin(string helperPath)
    {
        try
        {
            var packageFamily = GetCurrentProcessPackageFamily();
            var packageFullName = GetCurrentProcessPackageFullName();
            if (string.IsNullOrWhiteSpace(packageFamily)
                || string.IsNullOrWhiteSpace(packageFullName))
            {
                return false;
            }

            return IsEligiblePackageAdminHost(
                Environment.ProcessPath,
                packageFamily,
                GetRegisteredPackagePath(packageFullName),
                AppContext.BaseDirectory,
                helperPath,
                GetExpectedPackageFamily());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            return false;
        }
    }

    public static void VerifyTrustedAppServer(SafePipeHandle pipeHandle)
    {
        ArgumentNullException.ThrowIfNull(pipeHandle);
        if (!GetNamedPipeServerProcessId(pipeHandle, out var serverProcessId)
            || serverProcessId == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using var serverProcess = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            serverProcessId);
        if (serverProcess.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var actualImagePath = GetProcessImagePath(serverProcess);
        var serverPackageFamily = GetProcessPackageFamily(serverProcess);
        var serverPackageFullName = GetProcessPackageFullName(serverProcess);
        var serverPackageInstallPath = GetRegisteredPackagePath(serverPackageFullName);
        var helperPackageFamily = GetCurrentProcessPackageFamily();
        var expectedPackageFamily = GetExpectedPackageFamily();
        if (!IsTrustedServerIdentity(
                actualImagePath,
                serverPackageFamily,
                serverPackageInstallPath,
                AppContext.BaseDirectory,
                expectedPackageFamily,
                helperPackageFamily))
        {
            throw new UnauthorizedAccessException(
                "The elevated helper pipe was not created by the trusted packaged app.");
        }
    }

    internal static bool IsTrustedServerIdentity(
        string? actualImagePath,
        string? serverPackageFamily,
        string? serverPackageInstallPath,
        string? helperBaseDirectory,
        string expectedPackageFamily,
        string? helperPackageFamily)
    {
        if (string.IsNullOrWhiteSpace(actualImagePath)
            || string.IsNullOrWhiteSpace(serverPackageFamily)
            || string.IsNullOrWhiteSpace(serverPackageInstallPath)
            || string.IsNullOrWhiteSpace(helperBaseDirectory)
            || string.IsNullOrWhiteSpace(expectedPackageFamily))
        {
            return false;
        }

        string actual;
        string installed;
        string helperBase;
        try
        {
            actual = Path.GetFullPath(actualImagePath);
            installed = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(serverPackageInstallPath));
            helperBase = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(helperBaseDirectory));
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }

        if (!IsProtectedPackageInstallLocation(installed)
            || !string.Equals(installed, helperBase, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                actual,
                Path.Combine(installed, "PackagePilot.App.exe"),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(
                serverPackageFamily,
                expectedPackageFamily,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(helperPackageFamily)
            || string.Equals(
                serverPackageFamily,
                helperPackageFamily,
                StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsEligiblePackageAdminHost(
        string? actualImagePath,
        string? packageFamily,
        string? packageInstallPath,
        string? appBaseDirectory,
        string? helperPath,
        string expectedPackageFamily)
    {
        if (!IsTrustedServerIdentity(
                actualImagePath,
                packageFamily,
                packageInstallPath,
                appBaseDirectory,
                expectedPackageFamily,
                packageFamily)
            || string.IsNullOrWhiteSpace(packageInstallPath)
            || string.IsNullOrWhiteSpace(helperPath))
        {
            return false;
        }

        try
        {
            var installed = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(packageInstallPath));
            var actualHelper = Path.GetFullPath(helperPath);
            return string.Equals(
                actualHelper,
                Path.Combine(installed, "PackagePilot.PackageAdmin.exe"),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsProtectedPackageInstallLocation(string path)
    {
        try
        {
            var packageDirectory = new DirectoryInfo(Path.GetFullPath(path));
            var windowsApps = packageDirectory.Parent;
            if (windowsApps is null
                || !string.Equals(windowsApps.Name, "WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var windowsAppsParent = windowsApps.Parent;
            if (windowsAppsParent is null)
            {
                return false;
            }

            var programFiles = Path.TrimEndingDirectorySeparator(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            return IsLocalDriveRoot(windowsAppsParent)
                || (!string.IsNullOrWhiteSpace(programFiles)
                    && string.Equals(
                        Path.TrimEndingDirectorySeparator(windowsAppsParent.FullName),
                        programFiles,
                        StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsLocalDriveRoot(DirectoryInfo directory)
    {
        var fullPath = directory.FullName;
        var root = Path.GetPathRoot(fullPath);
        return !string.IsNullOrWhiteSpace(root)
            && root.Length >= 3
            && char.IsAsciiLetter(root[0])
            && root[1] == Path.VolumeSeparatorChar
            && !root.StartsWith(@"\\", StringComparison.Ordinal)
            && string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string GetProcessImagePath(SafeProcessHandle process)
    {
        var capacity = MaximumImagePathCharacters;
        var builder = new StringBuilder(capacity);
        if (!QueryFullProcessImageName(process, 0, builder, ref capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return builder.ToString();
    }

    private static string GetProcessPackageFamily(SafeProcessHandle process)
    {
        uint length = 0;
        var result = GetPackageFamilyName(process, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw result == AppModelErrorNoPackage
                ? new UnauthorizedAccessException(
                    "The Package Pilot pipe server does not have package identity.")
                : new Win32Exception(result);
        }

        var builder = new StringBuilder(checked((int)length));
        result = GetPackageFamilyName(process, ref length, builder);
        if (result != 0 || builder.Length == 0)
        {
            throw result == AppModelErrorNoPackage
                ? new UnauthorizedAccessException(
                    "The Package Pilot pipe server does not have package identity.")
                : new Win32Exception(result);
        }

        return builder.ToString();
    }

    private static string GetProcessPackageFullName(SafeProcessHandle process)
    {
        uint length = 0;
        var result = GetPackageFullName(process, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw result == AppModelErrorNoPackage
                ? new UnauthorizedAccessException(
                    "The Package Pilot pipe server does not have package identity.")
                : new Win32Exception(result);
        }

        var builder = new StringBuilder(checked((int)length));
        result = GetPackageFullName(process, ref length, builder);
        if (result != 0 || builder.Length == 0)
        {
            throw new Win32Exception(result);
        }

        return builder.ToString();
    }

    private static string GetRegisteredPackagePath(string packageFullName)
    {
        uint length = 0;
        var result = GetPackagePathByFullName(packageFullName, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw new Win32Exception(result);
        }

        var builder = new StringBuilder(checked((int)length));
        result = GetPackagePathByFullName(packageFullName, ref length, builder);
        if (result != 0 || builder.Length == 0)
        {
            throw new Win32Exception(result);
        }

        return builder.ToString();
    }

    private static string? GetCurrentProcessPackageFamily()
    {
        uint length = 0;
        var result = GetCurrentPackageFamilyName(ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            return null;
        }

        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw new Win32Exception(result);
        }

        var builder = new StringBuilder(checked((int)length));
        result = GetCurrentPackageFamilyName(ref length, builder);
        if (result != 0)
        {
            throw new Win32Exception(result);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? GetCurrentProcessPackageFullName()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            return null;
        }

        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw new Win32Exception(result);
        }

        var builder = new StringBuilder(checked((int)length));
        result = GetCurrentPackageFullName(ref length, builder);
        if (result != 0)
        {
            throw new Win32Exception(result);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string GetExpectedPackageFamily()
    {
        var identity = new NativePackageId
        {
            ProcessorArchitecture = ProcessorArchitectureUnknown,
            Name = ExpectedPackageName,
            Publisher = ExpectedPackagePublisher
        };
        uint length = 0;
        var result = PackageFamilyNameFromId(ref identity, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw new Win32Exception(result);
        }

        var builder = new StringBuilder(checked((int)length));
        result = PackageFamilyNameFromId(ref identity, ref length, builder);
        if (result != 0 || builder.Length == 0)
        {
            throw new Win32Exception(result);
        }

        return builder.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativePackageId
    {
        public uint Reserved;
        public uint ProcessorArchitecture;
        public ulong Version;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Publisher;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ResourceId;
        [MarshalAs(UnmanagedType.LPWStr)] public string? PublisherId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        int flags,
        StringBuilder imagePath,
        ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(
        SafeProcessHandle process,
        ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(
        SafeProcessHandle process,
        ref uint packageFullNameLength,
        StringBuilder? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(
        string packageFullName,
        ref uint pathLength,
        StringBuilder? path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(
        ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        StringBuilder? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageFamilyNameFromId(
        ref NativePackageId packageId,
        ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);
}
