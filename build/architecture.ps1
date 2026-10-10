# Verify payloads before staging or packaging them. Pure IL AnyCPU assemblies
# report I386; native and ReadyToRun binaries must match the target machine.
function Assert-NativeArchitecture([string] $Path, [string] $Platform, [switch] $AllowAnyCpu) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $headers = $reader.PEHeaders
        $machine = [int]$headers.CoffHeader.Machine
        if ($AllowAnyCpu -and $machine -eq 0x014C -and $null -ne $headers.CorHeader) {
            $flags = [int]$headers.CorHeader.Flags
            if (($flags -band 1) -ne 0 -and ($flags -band 2) -eq 0) { return }
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
