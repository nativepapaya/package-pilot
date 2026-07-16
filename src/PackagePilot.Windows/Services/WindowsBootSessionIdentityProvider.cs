using System.Runtime.InteropServices;

namespace PackagePilot.Windows.Services;

public sealed record BootSessionIdentityResult(
    string? Identity,
    string? Error)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(Identity);
}

/// <summary>
/// Reads the Windows boot sequence from KUSER_SHARED_DATA. The value is maintained by the
/// OS loader, is unaffected by wall-clock changes, and remains stable across app restarts.
/// </summary>
public sealed class WindowsBootSessionIdentityProvider
{
    // KUSER_SHARED_DATA is mapped read-only into every user process at 0x7FFE0000.
    // BootId is the documented ULONG field at offset 0x2C4 on supported Windows versions.
    private static readonly nint BootIdAddress = new(0x7FFE02C4L);

    public BootSessionIdentityResult GetCurrent()
    {
        try
        {
            var bootId = unchecked((uint)Marshal.ReadInt32(BootIdAddress));
            return new BootSessionIdentityResult(
                $"kuser-boot-v1:{bootId:X8}",
                null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new BootSessionIdentityResult(
                null,
                $"Windows boot-session identity is unavailable: {exception.Message}");
        }
    }
}
