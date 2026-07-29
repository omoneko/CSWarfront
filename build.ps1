$ErrorActionPreference = "Stop"
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild not found" }
& $msbuild "src\CSWarfront\CSWarfront.csproj" /t:Restore,Build /p:Configuration=Release /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$dll = "src\CSWarfront\bin\Release\CSWarfront.dll"
$modDir = Join-Path $env:LOCALAPPDATA "Colossal Order\Cities_Skylines\Addons\Mods\CSWarfront"
New-Item -ItemType Directory -Force -Path $modDir | Out-Null
Copy-Item $dll $modDir -Force

# Deploy sound assets (Sounds/*.wav). WarfrontSounds loads these at runtime.
# Note: CS (Unity 5.6) cannot decode MP3 at runtime, so only the converted *.wav files are deployed;
# the original *.mp3 files also live under src\CSWarfront\Sounds\ as source material but are not copied.
$soundsSrc = "src\CSWarfront\Sounds"
if (Test-Path $soundsSrc) {
    $soundsDst = Join-Path $modDir "Sounds"
    New-Item -ItemType Directory -Force -Path $soundsDst | Out-Null
    Copy-Item (Join-Path $soundsSrc "*") $soundsDst -Include *.wav -Force
    Write-Host "Sounds deployed: $soundsDst"
}

# Deploy built-in default models (Unit_*.obj/.mtl, Building_MilitaryBase.obj/.mtl).
# WarfrontModelProvider loads these at runtime (Task57).
$modelsSrc = "src\CSWarfront\Models"
if (Test-Path $modelsSrc) {
    $modelsDst = Join-Path $modDir "Models"
    New-Item -ItemType Directory -Force -Path $modelsDst | Out-Null
    Copy-Item (Join-Path $modelsSrc "*") $modelsDst -Include *.obj,*.mtl -Force
    Write-Host "Models deployed: $modelsDst"
}

Write-Host "Deployment complete: $modDir"
