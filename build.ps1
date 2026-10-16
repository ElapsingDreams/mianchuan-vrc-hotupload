param([switch]$ManagedOnly)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $csc)) { throw '.NET Framework 4.x compiler was not found.' }
$toolsDir = Join-Path $root 'tools'
$sourceDir = Join-Path $root 'src'
function Compile([string]$output, [string[]]$files, [string[]]$references, [string]$target = 'exe') {
    $arguments = @('-nologo', '-codepage:65001', "-target:$target", '-platform:x64', '-optimize+', "-out:$root\$output")
    foreach ($reference in $references) { $arguments += "-reference:$reference" }
    foreach ($file in $files) { $arguments += Join-Path $sourceDir $file }
    $arguments += Join-Path $sourceDir 'AssemblyInfo.cs'
    & $csc @arguments
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $output" }
    Write-Host "Built $output"
}
Push-Location -LiteralPath $root
try {
    if (!$ManagedOnly) {
        $cxx = (Get-Command g++.exe -ErrorAction Stop).Source
        $cc = (Get-Command gcc.exe -ErrorAction Stop).Source
        & $cxx '-O2' '-std=c++17' '-shared' '-static' '-static-libgcc' '-static-libstdc++' 'native/HotUploadNative.cpp' '-o' 'tools/HotUploadNative.dll'
        if ($LASTEXITCODE -ne 0) { throw 'Native CAB library compilation failed.' }
        $sdk = 'native/lzma-sdk/C'
        $lzmaSources = @('LzmaEnc.c','LzFind.c','LzFindMt.c','LzFindOpt.c','Threads.c','CpuArch.c','Alloc.c') | ForEach-Object { "$sdk/$_" }
        & $cc '-O3' '-DNDEBUG' '-DUNICODE' '-D_UNICODE' '-shared' '-static-libgcc' "-I$sdk" 'native/NativeLzma.c' @lzmaSources '-o' 'tools/NativeLzma.dll'
        if ($LASTEXITCODE -ne 0) { throw 'Native LZMA compilation failed.' }
    }
    foreach ($dll in @('HotUploadNative.dll','NativeLzma.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $toolsDir $dll))) { throw "Missing native library: $dll" }
    }
    $assets = @("$toolsDir\AssetsTools.NET.dll", "$toolsDir\netstandard.dll")
    Compile 'tools\HotswapTool.exe' @('HotswapTool.cs','NativeHotswapBridge.cs','NativeAvatarLzma.cs') $assets
    Compile 'tools\PreflightTool.exe' @('PreflightTool.cs') $assets
    Compile 'tools\WorldPreflightTool.exe' @('WorldPreflightTool.cs') $assets
    Compile 'tools\WorldSignTool.exe' @('WorldSignTool.cs') @()
    $refs = @("$framework\System.Web.Extensions.dll", "$root\librsync.net.dll", "$framework\System.Windows.Forms.dll", "$framework\System.Drawing.dll", "$framework\WPF\PresentationCore.dll", "$framework\WPF\WindowsBase.dll", "$framework\System.Xaml.dll", "$framework\System.Security.dll")
    Compile 'MianchuanUploader.exe' @('VrcaCore.cs','VrcaGui.cs','Theme.cs','WorkspaceLayout.cs','WorkspaceDiagnostics.cs','WorkspaceLogView.cs') $refs 'winexe'
} finally {
    Pop-Location
}
Write-Host 'Build complete: MianchuanUploader.exe'
