#Requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal {
    param(
        [AllowNull()]
        [object]$Actual,

        [AllowNull()]
        [object]$Expected,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not [object]::Equals($Actual, $Expected)) {
        throw "$Message Expected '$Expected', but found '$Actual'."
    }
}

$nativeCommandPath = Join-Path $PSScriptRoot '..\NativeCommand.ps1'
. $nativeCommandPath

$enginePath = if ($PSVersionTable.PSVersion.Major -ge 6) {
    Join-Path $PSHOME 'pwsh.exe'
}
else {
    Join-Path $PSHOME 'powershell.exe'
}
if (-not (Test-Path -LiteralPath $enginePath -PathType Leaf)) {
    throw "The current PowerShell executable was not found at '$enginePath'."
}

$testRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    "PackagePilot-NativeCommandTests-$([Guid]::NewGuid().ToString('N'))"
$childScriptPath = Join-Path $testRoot 'NativeCommandChild.ps1'

try {
    [void][IO.Directory]::CreateDirectory($testRoot)
    $childScript = @'
param(
    [Parameter(Mandatory)]
    [ValidateSet('Success', 'SuccessWithWarning', 'Fail', 'LargeFail', 'NoisyFail')]
    [string]$Mode,

    [string]$Value = '',

    [string]$Secret = ''
)

$encodedValue = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value))
switch ($Mode) {
    'Success' {
        [Console]::Out.WriteLine("OUT64:$encodedValue")
        exit 0
    }
    'SuccessWithWarning' {
        [Console]::Out.WriteLine("OUT64:$encodedValue")
        [Console]::Error.WriteLine("warning:$Secret")
        exit 0
    }
    'Fail' {
        [Console]::Out.WriteLine("OUT64:$encodedValue")
        [Console]::Error.WriteLine("failure:$Secret")
        exit 23
    }
    'LargeFail' {
        [Console]::Error.WriteLine(('x' * 600) + $Secret + ('y' * 20))
        exit 29
    }
    'NoisyFail' {
        foreach ($index in 1..2000) {
            [Console]::Error.WriteLine(
                ('noise-{0:D4}:{1}' -f $index, ('z' * 80)))
        }
        exit 31
    }
}
'@
    [IO.File]::WriteAllText(
        $childScriptPath,
        $childScript,
        [Text.UTF8Encoding]::new($true))

    $value = 'Resume folder with spaces & package'
    $expectedValue = 'OUT64:' + [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($value))
    $baseArguments = @(
        '-NoProfile'
        '-NonInteractive'
        '-ExecutionPolicy'
        'Bypass'
        '-File'
        $childScriptPath
    )

    $captured = @(Invoke-NativeChecked `
        -FilePath $enginePath `
        -ArgumentList ($baseArguments + @('-Mode', 'Success', '-Value', $value)) `
        -Activity 'Capture test' `
        -OutputMode Capture)
    Assert-Equal `
        -Actual $captured.Count `
        -Expected 1 `
        -Message 'Capture mode returned an unexpected number of stdout lines.'
    Assert-Equal `
        -Actual $captured[0] `
        -Expected $expectedValue `
        -Message 'Capture mode did not preserve the argument or stdout value.'

    $streamed = @(Invoke-NativeChecked `
        -FilePath $enginePath `
        -ArgumentList ($baseArguments + @('-Mode', 'Success', '-Value', $value)) `
        -Activity 'Stream test' `
        -OutputMode Stream)
    Assert-Equal `
        -Actual $streamed.Count `
        -Expected 1 `
        -Message 'Stream mode returned an unexpected number of stdout lines.'
    Assert-Equal `
        -Actual $streamed[0] `
        -Expected $expectedValue `
        -Message 'Stream mode did not preserve stdout.'

    $discarded = @(Invoke-NativeChecked `
        -FilePath $enginePath `
        -ArgumentList ($baseArguments + @('-Mode', 'Success', '-Value', $value)) `
        -Activity 'Discard test' `
        -OutputMode Discard)
    Assert-Equal `
        -Actual $discarded.Count `
        -Expected 0 `
        -Message 'Discard mode emitted stdout.'

    $warningSecret = 'warning-secret-value'
    $warnings = @()
    $warningOutput = @(Invoke-NativeChecked `
        -FilePath $enginePath `
        -ArgumentList ($baseArguments + @(
            '-Mode', 'SuccessWithWarning',
            '-Value', $value,
            '-Secret', $warningSecret)) `
        -Activity 'Successful stderr test' `
        -OutputMode Capture `
        -RedactedValues $warningSecret `
        -WarningVariable warnings)
    Assert-Equal `
        -Actual $warningOutput[0] `
        -Expected $expectedValue `
        -Message 'Successful stderr was mixed into captured stdout.'
    $warningText = $warnings -join [Environment]::NewLine
    Assert-True `
        -Condition ($warningText.Contains('<redacted>')) `
        -Message 'Successful stderr did not apply redaction.'
    Assert-True `
        -Condition (-not $warningText.Contains($warningSecret)) `
        -Message 'Successful stderr exposed a redacted value.'

    $failureSecret = 'failure-secret-value'
    $failure = $null
    try {
        Invoke-NativeChecked `
            -FilePath $enginePath `
            -ArgumentList ($baseArguments + @(
                '-Mode', 'Fail',
                '-Value', $value,
                '-Secret', $failureSecret)) `
            -Activity 'Expected failure test' `
            -OutputMode Capture `
            -RedactedValues $failureSecret | Out-Null
    }
    catch {
        $failure = $_
    }
    Assert-True `
        -Condition ($null -ne $failure) `
        -Message 'A nonzero native exit did not throw.'
    Assert-True `
        -Condition ($failure.Exception -is [InvalidOperationException]) `
        -Message 'A nonzero native exit did not produce the structured exception type.'
    Assert-Equal `
        -Actual $failure.Exception.Data['PackagePilot.NativeCommand.ExitCode'] `
        -Expected 23 `
        -Message 'The structured failure did not preserve the exact native exit code.'
    Assert-Equal `
        -Actual $failure.Exception.Data['PackagePilot.NativeCommand.Activity'] `
        -Expected 'Expected failure test' `
        -Message 'The structured failure did not preserve its safe activity label.'
    Assert-Equal `
        -Actual $failure.Exception.Data['PackagePilot.NativeCommand.Tool'] `
        -Expected ([IO.Path]::GetFileName($enginePath)) `
        -Message 'The structured failure did not identify the native tool.'
    Assert-True `
        -Condition (
            [long]$failure.Exception.Data['PackagePilot.NativeCommand.DurationMilliseconds'] -ge 0) `
        -Message 'The structured failure did not include a valid duration.'
    $failureDiagnostic = [string]$failure.Exception.Data[
        'PackagePilot.NativeCommand.Diagnostic']
    Assert-True `
        -Condition ($failureDiagnostic.Contains('<redacted>')) `
        -Message 'Failure diagnostics did not apply redaction.'
    Assert-True `
        -Condition (
            -not $failure.Exception.Message.Contains($failureSecret) -and
            -not $failureDiagnostic.Contains($failureSecret)) `
        -Message 'Failure diagnostics exposed a redacted value.'
    Assert-True `
        -Condition (-not $failure.Exception.Message.Contains($value)) `
        -Message 'The helper added its raw native argument list to the failure message.'

    $longSecret = 's' * 300
    $boundedFailure = $null
    try {
        Invoke-NativeChecked `
            -FilePath $enginePath `
            -ArgumentList ($baseArguments + @(
                '-Mode', 'LargeFail',
                '-Secret', $longSecret)) `
            -Activity 'Bounded diagnostic test' `
            -OutputMode Discard `
            -RedactedValues $longSecret `
            -DiagnosticCharacterLimit 256
    }
    catch {
        $boundedFailure = $_
    }
    Assert-True `
        -Condition ($null -ne $boundedFailure) `
        -Message 'The bounded diagnostic test did not fail as expected.'
    $boundedDiagnostic = [string]$boundedFailure.Exception.Data[
        'PackagePilot.NativeCommand.Diagnostic']
    Assert-True `
        -Condition ($boundedDiagnostic.Length -le 256) `
        -Message 'Failure diagnostics exceeded their configured bound.'
    Assert-True `
        -Condition ($boundedDiagnostic.Contains('<redacted>')) `
        -Message 'Diagnostics were truncated before the complete secret was redacted.'
    Assert-True `
        -Condition (-not $boundedDiagnostic.Contains(('s' * 20))) `
        -Message 'A partial secret remained after diagnostic truncation.'

    $noisyFailure = $null
    try {
        Invoke-NativeChecked `
            -FilePath $enginePath `
            -ArgumentList ($baseArguments + @('-Mode', 'NoisyFail')) `
            -Activity 'Noisy diagnostic test' `
            -OutputMode Discard `
            -DiagnosticCharacterLimit 512
    }
    catch {
        $noisyFailure = $_
    }
    Assert-True `
        -Condition ($null -ne $noisyFailure) `
        -Message 'The noisy diagnostic test did not fail as expected.'
    $noisyDiagnostic = [string]$noisyFailure.Exception.Data[
        'PackagePilot.NativeCommand.Diagnostic']
    Assert-True `
        -Condition (
            $noisyDiagnostic.Length -le 512 -and
            $noisyDiagnostic.Contains('diagnostic truncated') -and
            $noisyDiagnostic.Contains('noise-2000')) `
        -Message 'Noisy stderr was not retained as a bounded diagnostic tail.'

    Assert-Equal `
        -Actual $ErrorActionPreference.ToString() `
        -Expected 'Stop' `
        -Message 'The helper did not restore ErrorActionPreference.'

    $nativePreference = Get-Variable `
        -Name PSNativeCommandUseErrorActionPreference `
        -ErrorAction SilentlyContinue
    if ($null -ne $nativePreference) {
        $originalNativePreference = [bool]$nativePreference.Value
        try {
            Set-Variable `
                -Name PSNativeCommandUseErrorActionPreference `
                -Value $true
            $preferenceFailure = $null
            try {
                Invoke-NativeChecked `
                    -FilePath $enginePath `
                    -ArgumentList ($baseArguments + @(
                        '-Mode', 'Fail',
                        '-Secret', $failureSecret)) `
                    -Activity 'Native preference test' `
                    -OutputMode Discard `
                    -RedactedValues $failureSecret
            }
            catch {
                $preferenceFailure = $_
            }
            Assert-Equal `
                -Actual $preferenceFailure.Exception.Data[
                    'PackagePilot.NativeCommand.ExitCode'] `
                -Expected 23 `
                -Message 'PowerShell native-error preference bypassed explicit exit handling.'
            Assert-True `
                -Condition $PSNativeCommandUseErrorActionPreference `
                -Message 'The helper did not restore the caller native-error preference.'
        }
        finally {
            Set-Variable `
                -Name PSNativeCommandUseErrorActionPreference `
                -Value $originalNativePreference
        }
    }

    $missingName = "PackagePilot-Missing-$([Guid]::NewGuid().ToString('N')).exe"
    $missingFailure = $null
    try {
        Invoke-NativeChecked `
            -FilePath $missingName `
            -Activity 'Missing application test' `
            -OutputMode Discard
    }
    catch {
        $missingFailure = $_
    }
    Assert-True `
        -Condition ($null -ne $missingFailure) `
        -Message 'A missing application did not throw.'
    Assert-True `
        -Condition (
            $missingFailure.Exception -is [InvalidOperationException] -and
            -not $missingFailure.Exception.Data.Contains(
                'PackagePilot.NativeCommand.ExitCode')) `
        -Message 'A missing application reported a fabricated native exit code.'
    Assert-Equal `
        -Actual $missingFailure.Exception.Data['PackagePilot.NativeCommand.Tool'] `
        -Expected $missingName `
        -Message 'A missing application failure did not identify the requested tool.'

    $identitySecret = "secret-$([Guid]::NewGuid().ToString('N'))"
    $redactedFailure = $null
    try {
        Invoke-NativeChecked `
            -FilePath "$identitySecret.exe" `
            -Activity "Resolve $identitySecret" `
            -OutputMode Discard `
            -RedactedValues $identitySecret
    }
    catch {
        $redactedFailure = $_
    }
    Assert-True `
        -Condition ($null -ne $redactedFailure) `
        -Message 'The missing secret-named application did not fail.'
    Assert-True `
        -Condition (
            $redactedFailure.Exception.ToString().Contains('<redacted>') -and
            -not $redactedFailure.Exception.ToString().Contains($identitySecret) -and
            -not ([string]$redactedFailure.Exception.Data[
                'PackagePilot.NativeCommand.Tool']).Contains($identitySecret) -and
            -not ([string]$redactedFailure.Exception.Data[
                'PackagePilot.NativeCommand.Activity']).Contains($identitySecret)) `
        -Message 'Tool, activity, or nested exception context exposed a redacted value.'

    Write-Output "Native command tests passed on PowerShell $($PSVersionTable.PSVersion)."
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
