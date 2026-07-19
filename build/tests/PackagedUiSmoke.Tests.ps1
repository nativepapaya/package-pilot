[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [string]$DependencyPackagePath,

    [Parameter(Mandatory)]
    [string]$CertificateThumbprint,

    [Parameter(Mandatory)]
    [string]$ScreenshotDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$resolvedDependency = (Resolve-Path -LiteralPath $DependencyPackagePath).Path
$resolvedScreenshots = [IO.Path]::GetFullPath($ScreenshotDirectory)
[IO.Directory]::CreateDirectory($resolvedScreenshots) | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$nativeSource = @'
using System;
using System.Runtime.InteropServices;

public static class PackagedSmokeNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out RECT rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    public static int ActivateApplication(string appUserModelId, out uint processId)
    {
        IApplicationActivationManager manager =
            (IApplicationActivationManager)new ApplicationActivationManager();
        return manager.ActivateApplication(appUserModelId, string.Empty, 0, out processId);
    }

    public static void PressEnter()
    {
        const byte Enter = 0x0D;
        const uint KeyUp = 0x0002;
        keybd_event(Enter, 0, 0, UIntPtr.Zero);
        keybd_event(Enter, 0, KeyUp, UIntPtr.Zero);
    }
}

[ComImport]
[Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
public class ApplicationActivationManager
{
}

[ComImport]
[Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments,
        uint options,
        out uint processId);

    [PreserveSig]
    int ActivateForFile(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        IntPtr itemArray,
        [MarshalAs(UnmanagedType.LPWStr)] string verb,
        out uint processId);

    [PreserveSig]
    int ActivateForProtocol(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        IntPtr itemArray,
        out uint processId);
}
'@

Add-Type -TypeDefinition $nativeSource -Language CSharp

function Select-NavigationDestination {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $matches = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($matches.Count -eq 0) {
        throw "The packaged UI does not expose the '$Name' destination to UI Automation."
    }

    foreach ($element in $matches) {
        $pattern = $null
        if ($element.TryGetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$pattern)) {
            ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()
            return
        }

        if ($element.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$pattern)) {
            ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
            return
        }
    }

    throw "The '$Name' destination is visible but cannot be selected through UI Automation."
}

function Wait-DestinationPresented {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [string]$Destination,

        [Parameter(Mandatory)]
        [string]$MarkerName
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $MarkerName)
    $deadline = [DateTimeOffset]::Now.AddSeconds(10)
    do {
        $marker = $Root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
        if ($null -ne $marker -and -not $marker.Current.IsOffscreen) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::Now -lt $deadline)

    throw "The '$Destination' destination did not present its '$MarkerName' content within 10 seconds."
}

function Submit-DiscoverSearch {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [string]$Query
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        'Search packages')
    $search = $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $search) {
        throw 'The packaged Discover search control is not available to UI Automation.'
    }

    $editCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edit = $search.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $editCondition)
    if ($null -eq $edit) {
        throw 'The packaged Discover search edit control is not available to UI Automation.'
    }

    $pattern = $null
    if (-not $edit.TryGetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern,
        [ref]$pattern)) {
        throw 'The packaged Discover search edit control does not support UI Automation values.'
    }

    $edit.SetFocus()
    ([System.Windows.Automation.ValuePattern]$pattern).SetValue($Query)
    Start-Sleep -Milliseconds 100
    [PackagedSmokeNative]::PressEnter()
}

function Wait-DiscoverInstalledAction {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $deadline = [DateTimeOffset]::Now.AddSeconds(30)
    do {
        $buttons = $Root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
        foreach ($button in $buttons) {
            $name = $button.Current.Name
            $isInstalledAction =
                $name.StartsWith('View installed ', [StringComparison]::OrdinalIgnoreCase) -or
                $name.StartsWith('Update ', [StringComparison]::OrdinalIgnoreCase)
            $isEnabled = $button.Current.IsEnabled
            $isVisible = -not $button.Current.IsOffscreen
            if ($isInstalledAction -and $isEnabled -and $isVisible) {
                return
            }
        }

        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::Now -lt $deadline)

    throw "Discover did not recognize the runner's preinstalled 7zip.7zip package within 30 seconds."
}

function Save-WindowScreenshot {
    param(
        [Parameter(Mandatory)]
        [IntPtr]$Window,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $rectangle = [PackagedSmokeNative+RECT]::new()
    if (-not [PackagedSmokeNative]::GetWindowRect($Window, [ref]$rectangle)) {
        throw "GetWindowRect failed for screenshot '$Path'."
    }

    $width = $rectangle.Right - $rectangle.Left
    $height = $rectangle.Bottom - $rectangle.Top
    if ($width -lt 320 -or $height -lt 240) {
        throw "The packaged window has an invalid screenshot size: ${width}x${height}."
    }

    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen(
                $rectangle.Left,
                $rectangle.Top,
                0,
                0,
                [System.Drawing.Size]::new($width, $height))
        }
        finally {
            $graphics.Dispose()
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

$process = $null
$installedPackage = $null
try {
    Add-AppxPackage `
        -Path $resolvedPackage `
        -DependencyPath $resolvedDependency `
        -ForceApplicationShutdown

    $installedPackage = Get-AppxPackage PackagePilot.Desktop |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if ($null -eq $installedPackage -or $installedPackage.Status -ne 'Ok') {
        throw 'The signed Package Pilot smoke package is not registered and healthy.'
    }

    $appUserModelId = "$($installedPackage.PackageFamilyName)!App"
    [uint32]$processId = 0
    $activationResult = [PackagedSmokeNative]::ActivateApplication(
        $appUserModelId,
        [ref]$processId)
    if ($activationResult -ne 0 -or $processId -eq 0) {
        throw ('Package activation failed with HRESULT 0x{0:X8} and process ID {1}.' -f
            ([uint32]$activationResult),
            $processId)
    }

    $deadline = [DateTimeOffset]::Now.AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 250
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -ne $process) {
            $process.Refresh()
        }
    } while (($null -eq $process -or $process.MainWindowHandle -eq [IntPtr]::Zero) -and
        [DateTimeOffset]::Now -lt $deadline)

    if ($null -eq $process -or $process.HasExited) {
        throw 'The packaged Package Pilot process exited before exposing a window.'
    }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'The packaged Package Pilot process did not expose a window within 45 seconds.'
    }
    if (-not $process.Responding) {
        throw 'The packaged Package Pilot window is not responding.'
    }

    [void][PackagedSmokeNative]::ShowWindow($process.MainWindowHandle, 9)
    [void][PackagedSmokeNative]::SetForegroundWindow($process.MainWindowHandle)

    $automationRoot = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)
    if ($null -eq $automationRoot) {
        throw 'UI Automation could not bind to the packaged Package Pilot window.'
    }

    # Hosted Windows runners can expose the UI Automation tree before DWM has
    # committed the first complete WinUI surface. This delay is for artifact
    # capture only and is not part of application startup logic.
    Start-Sleep -Milliseconds 2500
    [void][PackagedSmokeNative]::DwmFlush()

    $destinations = [ordered]@{
        Discover = 'Search packages'
        Installed = 'Filter installed packages'
        Updates = 'Check for updates'
        Activity = 'Clear completed activity'
        Sources = 'Configured package sources'
        Settings = 'App theme'
    }
    foreach ($destination in $destinations.Keys) {
        Select-NavigationDestination -Root $automationRoot -Name $destination
        Wait-DestinationPresented `
            -Root $automationRoot `
            -Destination $destination `
            -MarkerName $destinations[$destination]
        if ($destination -eq 'Discover') {
            Submit-DiscoverSearch -Root $automationRoot -Query '7zip.7zip'
            Wait-DiscoverInstalledAction -Root $automationRoot
        }
        Start-Sleep -Milliseconds 500
        [void][PackagedSmokeNative]::DwmFlush()

        $process.Refresh()
        if ($process.HasExited -or -not $process.Responding) {
            throw "Package Pilot stopped responding after opening '$destination'."
        }

        [void][PackagedSmokeNative]::SetForegroundWindow($process.MainWindowHandle)
        $screenshotPath = Join-Path $resolvedScreenshots ($destination.ToLowerInvariant() + '.png')
        Save-WindowScreenshot -Window $process.MainWindowHandle -Path $screenshotPath
    }

    $expectedScreenshots = @(
        $destinations.Keys | ForEach-Object {
            Join-Path $resolvedScreenshots ($_.ToLowerInvariant() + '.png')
        }
    )
    foreach ($screenshot in $expectedScreenshots) {
        if (-not (Test-Path -LiteralPath $screenshot) -or
            (Get-Item -LiteralPath $screenshot).Length -lt 1024) {
            throw "The packaged smoke screenshot is missing or empty: '$screenshot'."
        }
    }

    Write-Output (
        "Packaged UI smoke passed for {0} ({1}); all six destinations presented verified content." -f
        $installedPackage.PackageFullName,
        $process.Id)
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }

    if ($null -ne $installedPackage) {
        Remove-AppxPackage -Package $installedPackage.PackageFullName -ErrorAction SilentlyContinue
    }

    $trustedPath = "Cert:\LocalMachine\TrustedPeople\$CertificateThumbprint"
    if (Test-Path -LiteralPath $trustedPath) {
        Remove-Item -LiteralPath $trustedPath -Force
    }

    $personalPath = "Cert:\CurrentUser\My\$CertificateThumbprint"
    if (Test-Path -LiteralPath $personalPath) {
        Remove-Item -LiteralPath $personalPath -Force
    }
}
