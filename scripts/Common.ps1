function Get-ProjectRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Get-OutputPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $projectRoot = Get-ProjectRoot
    return Join-Path $projectRoot $RelativePath.TrimStart(".\")
}

function Update-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Mutator
    )

    if (-not (Test-Path $Path)) {
        throw "JSON file not found: $Path"
    }

    $json = Get-Content -Path $Path -Raw | ConvertFrom-Json
    & $Mutator $json
    $json | ConvertTo-Json -Depth 20 | Set-Content -Path $Path -Encoding UTF8
}

function Get-InnoSetupCompiler {
    $candidates = @(@(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue),
        (Get-Command ISCC -ErrorAction SilentlyContinue)
    ) | Where-Object { $null -ne $_ } | Select-Object -ExpandProperty Source -Unique)

    $knownPaths = @(@(
        "C:\Users\Administrator\AppData\Local\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) })

    $resolvedPath = @($candidates) + @($knownPaths) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($resolvedPath)) {
        throw "ISCC not found. Install Inno Setup and ensure ISCC.exe is available."
    }

    return $resolvedPath
}
