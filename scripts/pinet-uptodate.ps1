# Exit code contract (used by run.bat):
#   0 = the executable is up to date (no source newer than the binary)
#   1 = at least one source/project file is newer than the executable
#   2 = the executable is missing, or the source tree is unreadable
param(
    [Parameter(Mandatory = $true)]
    [string]$Exe,

    [string[]]$SourceRoots = @('src'),
    [string]$Pattern = '*.cs, *.csproj, *.slnx',

    # Directories whose timestamps must be ignored (build outputs regenerate
    # bin/obj copies of sources; comparing against them breaks staleness).
    [string[]]$IgnoredPathXs = @('\\bin\\', '\\obj\\')
)

$ErrorActionPreference = 'SilentlyContinue'

if (-not (Test-Path -LiteralPath $Exe)) {
    exit 2
}

$exeTime = (Get-Item -LiteralPath $Exe).LastWriteTimeUtc

$patterns = $Pattern -split ',' | ForEach-Object { $_.Trim() }
$files = foreach ($root in $SourceRoots) {
    if (-not (Test-Path -LiteralPath $root)) { continue }
    foreach ($p in $patterns) {
        Get-ChildItem -Path $root -Recurse -File -Filter $p
    }
}

# bin/obj copies (e.g. obj\...\GlobalUsings.g.cs) must not count: every build
$files = $files | Where-Object {
    $full = $_.FullName
    $skip = $false
    foreach ($x in $IgnoredPathXs) {
        if ($full -match $x) { $skip = $true; break }
    }
    -not $skip
}

if (-not $files) {
    exit 2
}

foreach ($f in $files) {
    if ($f.LastWriteTimeUtc -gt $exeTime) {
        exit 1
    }
}

exit 0
