<#
.SYNOPSIS
    Sets the Windows Lock Screen background. Called by WallpaperPicker.exe.
.DESCRIPTION
    The Lock Screen API is WinRT, which is much simpler to reach from Windows PowerShell than from
    the picker itself. Run with powershell.exe (not pwsh): WinRT types are only available there.
.PARAMETER ImagePath
    Full path of the image to use.
#>

[CmdletBinding()]
param(
    [Parameter(Position=0, Mandatory=$true)]
    [string]$ImagePath
)

try {
    $ImagePath = Convert-Path -LiteralPath $ImagePath

    # Pre-load Windows Runtime (WinRT) projected types
    [Windows.System.UserProfile.LockScreen, Windows.System.UserProfile, ContentType = WindowsRuntime] | Out-Null
    [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
    Add-Type -AssemblyName System.Runtime.WindowsRuntime

    $asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    })[0]

    $asTaskAction = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'
    })[0]

    # Get StorageFile asynchronously
    $getFileAsync = [Windows.Storage.StorageFile]::GetFileFromPathAsync($ImagePath)
    $getFileTask = $asTaskGeneric.MakeGenericMethod([Windows.Storage.StorageFile]).Invoke($null, @($getFileAsync))
    $getFileTask.Wait()
    $storageFile = $getFileTask.Result

    # Set LockScreen asynchronously
    $setLockAsync = [Windows.System.UserProfile.LockScreen]::SetImageFileAsync($storageFile)
    $setLockTask = $asTaskAction.Invoke($null, @($setLockAsync))
    $setLockTask.Wait()

    Write-Host "[+] Lock Screen background updated to: $ImagePath" -ForegroundColor Green
}
catch {
    Write-Error "Failed to set Lock Screen image: $_"
    exit 1
}
