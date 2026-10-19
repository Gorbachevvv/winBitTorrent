# Verify payloads before staging or packaging them. Pure IL AnyCPU assemblies
# report I386; native and ReadyToRun binaries must match the target machine.
function Invoke-BuildTool([string] $FilePath, [string[]] $Arguments) {
    $command = Get-Command $FilePath -CommandType Application -ErrorAction Stop
    # Windows PowerShell 5.1 turns redirected native stderr into error records.
    # Preserve diagnostics, but let the tool's exit code determine failure.
    $ErrorActionPreference = 'Continue'
    & $command.Source @Arguments 2>&1 | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) {
            if ($_.Exception.Message) { $_.Exception.Message }
        }
        else { $_.ToString() }
    }
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed with exit code $LASTEXITCODE." }
}

function Assert-MsvcBuildTools([string] $Platform) {
    $compiler = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Filter cl.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object FullName -Match "\\bin\\Host[^\\]+\\$Platform\\cl\.exe$" |
        Select-Object -First 1
    if (-not $compiler) {
        throw "MSVC $Platform compiler not found. Open Visual Studio Installer and add the MSVC C++ $Platform build tools and Windows SDK, then run this command again."
    }
}

function Assert-NativeArchitecture([string] $Path, [string] $Platform, [switch] $AllowAnyCpu) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Not a Windows executable: $Path" }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "Invalid PE header: $Path" }
        $machine = [int]$reader.ReadUInt16()
        $sectionCount = $reader.ReadUInt16()
        $stream.Position = $peOffset + 20
        $optionalHeaderSize = $reader.ReadUInt16()
        $optionalHeader = $peOffset + 24
        # Read the CLR flags directly so this also works in Windows PowerShell 5.1,
        # used by the existing Visual Studio External Tools command.
        if ($AllowAnyCpu -and $machine -eq 0x014C) {
            $stream.Position = $optionalHeader
            $magic = $reader.ReadUInt16()
            $dataDirectoryOffset = if ($magic -eq 0x10B) { 96 } elseif ($magic -eq 0x20B) { 112 } else { 0 }
            if ($dataDirectoryOffset -gt 0 -and $optionalHeaderSize -ge ($dataDirectoryOffset + 120)) {
                $stream.Position = $optionalHeader + $dataDirectoryOffset + 112
                $clrRva = $reader.ReadUInt32()
                if ($clrRva -ne 0) {
                    for ($index = 0; $index -lt $sectionCount; $index++) {
                        $stream.Position = $optionalHeader + $optionalHeaderSize + $index * 40 + 8
                        $virtualSize = $reader.ReadUInt32()
                        $virtualAddress = $reader.ReadUInt32()
                        $rawSize = $reader.ReadUInt32()
                        $rawOffset = $reader.ReadUInt32()
                        if ($clrRva -ge $virtualAddress -and $clrRva -lt ($virtualAddress + [Math]::Max($virtualSize, $rawSize))) {
                            $stream.Position = $rawOffset + ($clrRva - $virtualAddress) + 16
                            $flags = $reader.ReadUInt32()
                            if (($flags -band 1) -ne 0 -and ($flags -band 2) -eq 0) { return }
                            break
                        }
                    }
                }
            }
        }
        $expected = if ($Platform -eq 'arm64') { 0xAA64 } else { 0x8664 }
        if ($machine -ne $expected) {
            throw "Wrong architecture in '$Path': PE machine 0x$($machine.ToString('X4')), expected $Platform. Rebuild the runtime for -Platform $Platform."
        }
    }
    finally { $reader.Dispose() }
}

function Assert-RuntimeArchitecture([string] $EngineRoot, [string] $PythonRoot, [string] $Platform) {
    Assert-NativeArchitecture (Join-Path $EngineRoot 'WinBitTorrent.Native.dll') $Platform
    Assert-NativeArchitecture (Join-Path $EngineRoot 'torrent-rasterbar.dll') $Platform
    foreach ($binary in Get-ChildItem -LiteralPath $EngineRoot -Filter '*.dll' -File) {
        Assert-NativeArchitecture $binary.FullName $Platform -AllowAnyCpu
    }
    Assert-NativeArchitecture (Join-Path $PythonRoot 'python.exe') $Platform
    foreach ($binary in Get-ChildItem -LiteralPath $PythonRoot -File | Where-Object Extension -In '.dll','.pyd','.exe') {
        Assert-NativeArchitecture $binary.FullName $Platform
    }
}
