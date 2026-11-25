$ErrorActionPreference = "Stop"

$SdkRoot = Join-Path $env:LOCALAPPDATA "Android\Sdk"
$BuildTools = Join-Path $SdkRoot "build-tools\36.0.0"
$AndroidJar = Join-Path $SdkRoot "platforms\android-37.0\android.jar"
$JavaBin = "C:\Program Files\Android\Android Studio\jbr\bin"

$Aapt2 = Join-Path $BuildTools "aapt2.exe"
$D8 = Join-Path $BuildTools "d8.bat"
$ZipAlign = Join-Path $BuildTools "zipalign.exe"
$ApkSigner = Join-Path $BuildTools "apksigner.bat"
$Javac = Join-Path $JavaBin "javac.exe"
$Keytool = Join-Path $JavaBin "keytool.exe"

$AppRoot = $PSScriptRoot
$BuildDir = Join-Path $AppRoot "build"
$CompiledRes = Join-Path $BuildDir "res_compiled"
$GenSrc = Join-Path $BuildDir "gen"
$ClassesDir = Join-Path $BuildDir "classes"
$DexDir = Join-Path $BuildDir "dex"
$Manifest = Join-Path $AppRoot "app\src\main\AndroidManifest.xml"
$ResDir = Join-Path $AppRoot "app\src\main\res"
$JavaRoot = Join-Path $AppRoot "app\src\main\java"

New-Item -ItemType Directory -Force -Path $BuildDir, $CompiledRes, $GenSrc, $ClassesDir, $DexDir | Out-Null

# Clean stale bytecode: classes left over from an older package name both bloat
# the d8 command line and can sneak into the APK.
Get-ChildItem -Path $ClassesDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $DexDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "[1/6] Compiling Android resources with aapt2..."
& $Aapt2 compile --dir $ResDir -o (Join-Path $CompiledRes "res.zip")
if ($LASTEXITCODE -ne 0) { throw "aapt2 compile failed" }

$ResApk = Join-Path $BuildDir "res-only.apk"
Write-Host "[2/6] Linking manifest and generating R.java..."
& $Aapt2 link `
    --manifest $Manifest `
    -I $AndroidJar `
    (Join-Path $CompiledRes "res.zip") `
    --java $GenSrc `
    -o $ResApk `
    --auto-add-overlay
if ($LASTEXITCODE -ne 0) { throw "aapt2 link failed" }

Write-Host "[3/6] Compiling Java 11 sources..."
$JavaFiles = @(Get-ChildItem -Path $GenSrc, $JavaRoot -Recurse -Filter "*.java" | ForEach-Object { $_.FullName })
& $Javac --release 11 -classpath $AndroidJar -d $ClassesDir @JavaFiles
if ($LASTEXITCODE -ne 0) { throw "javac compilation failed" }

Write-Host "[4/6] Dexing bytecode with d8..."
$ClassFiles = @(Get-ChildItem -Path $ClassesDir -Recurse -Filter "*.class" | ForEach-Object { $_.FullName })
# One jar instead of thousands of arguments: the raw .class list is longer than
# the Windows command line can carry once the package has a few classes.
$ClassesArchive = Join-Path $BuildDir "classes.zip"
if (Test-Path $ClassesArchive) { Remove-Item $ClassesArchive -Force }
if ($ClassFiles.Count -gt 0) {
    Compress-Archive -Path $ClassFiles -DestinationPath $ClassesArchive -CompressionLevel Optimal -Force
}
& $D8 --release --min-api 28 --lib $AndroidJar --output $DexDir $ClassesArchive
if ($LASTEXITCODE -ne 0) { throw "d8 failed" }

Write-Host "[5/6] Packaging APK and zipaligning..."
$UnalignedApk = Join-Path $BuildDir "OpenPixelDeck-unaligned.apk"
$AlignedApk = Join-Path $BuildDir "OpenPixelDeck-aligned.apk"
$FinalApk = Join-Path $BuildDir "OpenPixelDeck-debug.apk"
Copy-Item -Force $ResApk $UnalignedApk

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$Zip = [System.IO.Compression.ZipFile]::Open($UnalignedApk, [System.IO.Compression.ZipArchiveMode]::Update)
$DexPath = Join-Path $DexDir "classes.dex"
$Entry = $Zip.GetEntry("classes.dex")
if ($Entry) { $Entry.Delete() }
[System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($Zip, $DexPath, "classes.dex") | Out-Null
$Zip.Dispose()

& $ZipAlign -f 4 $UnalignedApk $AlignedApk
if ($LASTEXITCODE -ne 0) { throw "zipalign failed" }

$Keystore = Join-Path $BuildDir "debug.keystore"
if (-not (Test-Path $Keystore)) {
    & $Keytool -genkey -v -keystore $Keystore -storepass android -alias androiddebugkey -keypass android -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=OpenPixelDeck,O=OpenPixel,C=US" | Out-Null
}

Write-Host "[6/6] Signing & verifying APK..."
& $ApkSigner sign --ks $Keystore --ks-pass pass:android --key-pass pass:android --out $FinalApk $AlignedApk
if ($LASTEXITCODE -ne 0) { throw "apksigner sign failed" }

& $ApkSigner verify --print-certs $FinalApk
if ($LASTEXITCODE -ne 0) { throw "apksigner verify failed" }

Write-Host "ANDROID APK READY: $FinalApk"
