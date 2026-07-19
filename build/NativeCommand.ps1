#Requires -Version 5.1

<#
.SYNOPSIS
Runs an external application with consistent exit handling and safe diagnostics.

.DESCRIPTION
Invoke-NativeChecked is the single boundary for text-based native build tools.
It keeps stdout separate from stderr, validates the exact process exit code, and
never adds the native argument list to an error message. Supplied sensitive
values are removed before diagnostic text is bounded.

The function intentionally performs its own exit-code check on every supported
PowerShell version. It temporarily neutralizes PowerShell 7's native-command
error preference so callers receive the same structured failure under Windows
PowerShell 5.1 and PowerShell 7.
#>
function Invoke-NativeChecked {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$FilePath,

        [Parameter()]
        [AllowEmptyCollection()]
        [string[]]$ArgumentList = @(),

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Activity,

        [Parameter()]
        [ValidateSet('Stream', 'Capture', 'Discard')]
        [string]$OutputMode = 'Stream',

        [Parameter()]
        [AllowEmptyCollection()]
        [string[]]$RedactedValues = @(),

        [Parameter()]
        [ValidateRange(128, 65536)]
        [int]$DiagnosticCharacterLimit = 4096
    )

    $redactions = @(
        $RedactedValues |
            Where-Object { -not [string]::IsNullOrEmpty($_) } |
            Select-Object -Unique |
            Sort-Object Length -Descending
    )
    $redact = {
        param(
            [AllowNull()]
            [string]$Text
        )

        if ($null -eq $Text) {
            return ''
        }

        $safeText = $Text
        foreach ($value in $redactions) {
            $safeText = $safeText.Replace($value, '<redacted>')
        }
        return $safeText
    }
    $boundDiagnostic = {
        param(
            [AllowNull()]
            [string]$Text
        )

        if ([string]::IsNullOrWhiteSpace($Text)) {
            return ''
        }
        if ($Text.Length -le $DiagnosticCharacterLimit) {
            return $Text
        }

        $marker = "[... diagnostic truncated ...]$([Environment]::NewLine)"
        if ($marker.Length -ge $DiagnosticCharacterLimit) {
            return $Text.Substring($Text.Length - $DiagnosticCharacterLimit)
        }

        $tailLength = $DiagnosticCharacterLimit - $marker.Length
        return $marker + $Text.Substring($Text.Length - $tailLength)
    }
    $safeActivity = & $redact $Activity
    $newFailure = {
        param(
            [Parameter(Mandatory)]
            [string]$Message,

            [AllowNull()]
            [object]$ExitCode,

            [AllowNull()]
            [string]$Diagnostic,

            [AllowNull()]
            [System.Exception]$InnerException,

            [Parameter(Mandatory)]
            [long]$DurationMilliseconds,

            [Parameter(Mandatory)]
            [string]$ToolName
        )

        $boundedDiagnostic = & $boundDiagnostic $Diagnostic
        $completeMessage = $Message
        if (-not [string]::IsNullOrWhiteSpace($boundedDiagnostic)) {
            $completeMessage += [Environment]::NewLine + $boundedDiagnostic
        }

        $safeInnerException = $null
        if ($null -ne $InnerException) {
            $safeInnerException = [InvalidOperationException]::new(
                (& $redact $InnerException.Message))
        }

        if ($null -eq $safeInnerException) {
            $exception = [InvalidOperationException]::new($completeMessage)
        }
        else {
            $exception = [InvalidOperationException]::new($completeMessage, $safeInnerException)
        }
        $exception.Data['PackagePilot.NativeCommand.Activity'] = $safeActivity
        $exception.Data['PackagePilot.NativeCommand.Tool'] = $ToolName
        $exception.Data['PackagePilot.NativeCommand.DurationMilliseconds'] =
            $DurationMilliseconds
        $exception.Data['PackagePilot.NativeCommand.Diagnostic'] = $boundedDiagnostic
        if ($null -ne $ExitCode) {
            $exception.Data['PackagePilot.NativeCommand.ExitCode'] = [int]$ExitCode
        }
        return $exception
    }

    $toolName = $FilePath
    try {
        $candidateName = [IO.Path]::GetFileName($FilePath)
        if (-not [string]::IsNullOrWhiteSpace($candidateName)) {
            $toolName = $candidateName
        }
    }
    catch {
        # Use the supplied command name only if it cannot be interpreted as a path.
    }
    $safeToolName = & $redact $toolName

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $commands = @(Get-Command -Name $FilePath -CommandType Application -ErrorAction Stop)
        if ($commands.Count -eq 0) {
            throw "No application command named '$toolName' was found."
        }
        $resolvedPath = [string]$commands[0].Source
        if ([string]::IsNullOrWhiteSpace($resolvedPath)) {
            $resolvedPath = [string]$commands[0].Path
        }
        if ([string]::IsNullOrWhiteSpace($resolvedPath)) {
            throw "Application command '$toolName' did not resolve to an executable path."
        }
    }
    catch {
        $stopwatch.Stop()
        $diagnostic = & $redact $_.Exception.Message
        throw (& $newFailure `
            -Message "$safeActivity could not start because '$safeToolName' was not found or is not an application." `
            -ExitCode $null `
            -Diagnostic $diagnostic `
            -InnerException $_.Exception `
            -DurationMilliseconds $stopwatch.ElapsedMilliseconds `
            -ToolName $safeToolName)
    }

    $capturedStandardOutput = [Collections.Generic.List[string]]::new()
    $diagnosticState = [pscustomobject]@{
        Builder = [Text.StringBuilder]::new()
        Truncated = $false
    }
    $appendDiagnostic = {
        param(
            [AllowNull()]
            [string]$Text
        )

        if ([string]::IsNullOrWhiteSpace($Text)) {
            return
        }

        $safeText = & $redact $Text
        if ($safeText.Length -gt $DiagnosticCharacterLimit) {
            $safeText = $safeText.Substring(
                $safeText.Length - $DiagnosticCharacterLimit)
            $diagnosticState.Truncated = $true
        }

        $separatorLength = if ($diagnosticState.Builder.Length -eq 0) {
            0
        }
        else {
            [Environment]::NewLine.Length
        }
        $excess =
            $diagnosticState.Builder.Length +
            $separatorLength +
            $safeText.Length -
            $DiagnosticCharacterLimit
        if ($excess -gt 0) {
            [void]$diagnosticState.Builder.Remove(
                0,
                [Math]::Min($excess, $diagnosticState.Builder.Length))
            $diagnosticState.Truncated = $true
        }
        if ($diagnosticState.Builder.Length -ne 0) {
            [void]$diagnosticState.Builder.Append([Environment]::NewLine)
        }
        [void]$diagnosticState.Builder.Append($safeText)

        if ($diagnosticState.Builder.Length -gt $DiagnosticCharacterLimit) {
            [void]$diagnosticState.Builder.Remove(
                0,
                $diagnosticState.Builder.Length - $DiagnosticCharacterLimit)
            $diagnosticState.Truncated = $true
        }
    }
    $invocationFailure = $null
    $exitCode = $null
    $previousErrorActionPreference = $ErrorActionPreference
    $nativeErrorPreferenceVariable = Get-Variable `
        -Name PSNativeCommandUseErrorActionPreference `
        -ErrorAction SilentlyContinue
    $previousNativeErrorPreference = $null

    try {
        $ErrorActionPreference = 'Continue'
        if ($null -ne $nativeErrorPreferenceVariable) {
            $previousNativeErrorPreference = [bool]$nativeErrorPreferenceVariable.Value
            Set-Variable `
                -Name PSNativeCommandUseErrorActionPreference `
                -Scope Local `
                -Value $false
        }

        try {
            # PowerShell updates LASTEXITCODE in runspace-global scope for native
            # processes. Clear that same slot so a launch failure cannot reuse a
            # successful exit code left by an earlier command.
            $global:LASTEXITCODE = $null
            & $resolvedPath @ArgumentList 2>&1 | ForEach-Object {
                if ($_ -is [System.Management.Automation.ErrorRecord]) {
                    & $appendDiagnostic $_.ToString()
                    return
                }

                $safeOutput = & $redact ([string]$_)
                switch ($OutputMode) {
                    'Stream' {
                        Write-Output $safeOutput
                    }
                    'Capture' {
                        [void]$capturedStandardOutput.Add($safeOutput)
                    }
                    'Discard' {
                        # Deliberately consume successful stdout.
                    }
                }
            }
            if ($null -ne $LASTEXITCODE) {
                $exitCode = [int]$LASTEXITCODE
            }
        }
        catch {
            $invocationFailure = $_.Exception
            & $appendDiagnostic $_.Exception.Message
        }
    }
    finally {
        if ($null -ne $nativeErrorPreferenceVariable) {
            Set-Variable `
                -Name PSNativeCommandUseErrorActionPreference `
                -Scope Local `
                -Value $previousNativeErrorPreference
        }
        $ErrorActionPreference = $previousErrorActionPreference
        $stopwatch.Stop()
    }

    $diagnostic = $diagnosticState.Builder.ToString()
    if ($diagnosticState.Truncated) {
        $diagnostic = & $boundDiagnostic (
            "[... diagnostic truncated ...]$([Environment]::NewLine)$diagnostic")
    }
    if ($null -ne $invocationFailure) {
        throw (& $newFailure `
            -Message "$safeActivity failed while '$safeToolName' was running." `
            -ExitCode $exitCode `
            -Diagnostic $diagnostic `
            -InnerException $invocationFailure `
            -DurationMilliseconds $stopwatch.ElapsedMilliseconds `
            -ToolName $safeToolName)
    }
    if ($null -eq $exitCode) {
        throw (& $newFailure `
            -Message "$safeActivity failed because '$safeToolName' did not report an exit code." `
            -ExitCode $null `
            -Diagnostic $diagnostic `
            -InnerException $null `
            -DurationMilliseconds $stopwatch.ElapsedMilliseconds `
            -ToolName $safeToolName)
    }
    if ($exitCode -ne 0) {
        throw (& $newFailure `
            -Message "$safeActivity failed because '$safeToolName' exited with code $exitCode." `
            -ExitCode $exitCode `
            -Diagnostic $diagnostic `
            -InnerException $null `
            -DurationMilliseconds $stopwatch.ElapsedMilliseconds `
            -ToolName $safeToolName)
    }

    if ($OutputMode -ne 'Discard') {
        if (-not [string]::IsNullOrWhiteSpace($diagnostic)) {
            Write-Warning $diagnostic
        }
    }
    if ($OutputMode -eq 'Capture') {
        foreach ($line in $capturedStandardOutput) {
            Write-Output $line
        }
    }
}
