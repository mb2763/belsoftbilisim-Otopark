# =====================================================================
#  KAPALI OTOPARK - PLAKA MOTORU TANI BETIGI  (17.09.2026)  -  SALT OKUMA
#
#  NE ICIN: Otopark.Client plaka OKUYAMIYOR (log: "Plaka yok" + "ONNX
#  detector yuklenemedi: The type initializer for
#  'Microsoft.ML.OnnxRuntime.NativeMethods' threw an exception" +
#  "Tesseract baslatilamadi"). Ayni exe baska bilgisayarda calisiyorsa
#  sorun KODDA DEGIL, BU MAKINEDEKI EKSIK BILESENDEDIR.
#
#  ONNX Runtime ve Tesseract iki AYRI motordur ama ORTAK bagimliligi
#  Microsoft Visual C++ Runtime'dir. Ikisi birden duserse ilk suclu odur.
#
#  KULLANIM (sorunlu bilgisayarda, normal PowerShell yeter):
#     Set-ExecutionPolicy -Scope Process Bypass -Force
#     .\plaka_motoru_tani.ps1
#     .\plaka_motoru_tani.ps1 -Klasor "D:\KapaliOtopark"   # exe baska yerdeyse
#
#  Ciktiyi (ekrandaki ozeti) gonderin.
# =====================================================================
param(
    [string]$Klasor = "D:\KapaliOtopark",
    [string]$ModelKlasoru = "C:\Otopark"
)

$ErrorActionPreference = "Continue"
function Baslik($m) { Write-Host "`n===== $m =====" -ForegroundColor Cyan }
function Iyi($m)    { Write-Host "  [TAMAM] $m" -ForegroundColor Green }
function Kotu($m)   { Write-Host "  [EKSIK] $m" -ForegroundColor Red }
function Bilgi($m)  { Write-Host "  $m" }

$sorunlar = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------------
Baslik "1) UYGULAMA KLASORU"
if (-not (Test-Path $Klasor)) {
    Kotu "Uygulama klasoru bulunamadi: $Klasor  (-Klasor ile dogru yolu verin)"
    $sorunlar.Add("Uygulama klasoru yok: $Klasor")
} else {
    Bilgi "Klasor: $Klasor"
    $exe = Join-Path $Klasor "Otopark.Client.exe"
    if (Test-Path $exe) {
        $fi = Get-Item $exe
        Iyi ("Otopark.Client.exe  {0:N0} KB  {1}" -f ($fi.Length/1KB), $fi.LastWriteTime)
    } else { Kotu "Otopark.Client.exe YOK"; $sorunlar.Add("exe yok") }
}

# ---------------------------------------------------------------------
Baslik "2) MOTOR DOSYALARI (uygulama klasorunde)"
$gerekli = @(
    @{ Ad = "onnxruntime.dll";               Yol = "onnxruntime.dll";                 Kritik = $true },
    @{ Ad = "Microsoft.ML.OnnxRuntime.dll";  Yol = "Microsoft.ML.OnnxRuntime.dll";    Kritik = $true },
    @{ Ad = "OpenCvSharpExtern.dll";         Yol = "OpenCvSharpExtern.dll";           Kritik = $true },
    @{ Ad = "x64\tesseract50.dll";           Yol = "x64\tesseract50.dll";             Kritik = $false },
    @{ Ad = "x64\leptonica-1.82.0.dll";      Yol = "x64\leptonica-1.82.0.dll";        Kritik = $false }
)
foreach ($g in $gerekli) {
    $tam = Join-Path $Klasor $g.Yol
    if (Test-Path $tam) { Iyi ("{0,-30} {1,10:N0} KB" -f $g.Ad, ((Get-Item $tam).Length/1KB)) }
    else {
        Kotu "$($g.Ad) YOK"
        if ($g.Kritik) { $sorunlar.Add("Motor dosyasi eksik: $($g.Ad)  (antivirus silmis olabilir)") }
    }
}

# ---------------------------------------------------------------------
Baslik "3) MODEL DOSYALARI ($ModelKlasoru)"
foreach ($m in @("models\plate_detector.onnx", "models\plate_ocr.onnx", "models\haarcascade_russian_plate_number.xml", "tessdata\eng.traineddata")) {
    $tam = Join-Path $ModelKlasoru $m
    if (Test-Path $tam) { Iyi ("{0,-45} {1,8:N1} MB" -f $m, ((Get-Item $tam).Length/1MB)) }
    else { Kotu "$m YOK"; $sorunlar.Add("Model dosyasi eksik: $m") }
}

# ---------------------------------------------------------------------
Baslik "4) VISUAL C++ RUNTIME (ASIL SUPHELI)"
# ONNX Runtime ve Tesseract bunlari kullanir. Biri eksikse IKISI BIRDEN duser.
$vcDosya = @("vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll")
$sistem = Join-Path $env:windir "System32"
foreach ($d in $vcDosya) {
    $tam = Join-Path $sistem $d
    if (Test-Path $tam) { Iyi ("{0,-22} {1}" -f $d, (Get-Item $tam).VersionInfo.FileVersion) }
    else {
        Kotu "$d YOK  (System32)"
        $sorunlar.Add("VC++ Runtime dosyasi eksik: $d -> 'Microsoft Visual C++ 2015-2022 Redistributable (x64)' kurun")
    }
}
$vcKey = 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'
if (Test-Path $vcKey) {
    $v = Get-ItemProperty $vcKey
    Iyi ("VC++ x64 Redistributable kayitli: Installed={0} Version={1}" -f $v.Installed, $v.Version)
} else {
    Kotu "VC++ x64 Redistributable KAYITLI DEGIL"
    $sorunlar.Add("VC++ x64 Redistributable kurulu degil -> vc_redist.x64.exe kurun")
}

# ---------------------------------------------------------------------
Baslik "5) GERCEK YUKLEME TESTI (Windows hata kodu ile)"
# LoadLibrary basarisiz olursa hata kodu sebebi soyler:
#   126 = bagimlilik bulunamadi (genelde VC++ Runtime)
#   193 = yanlis mimari (32/64 bit)
#     5 = erisim reddedildi (antivirus / yetki)
$sig = @"
[DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
public static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);
"@
try { Add-Type -MemberDefinition $sig -Name K32Tani -Namespace PlakaTani -PassThru | Out-Null } catch {}
foreach ($d in @("onnxruntime.dll", "OpenCvSharpExtern.dll", "x64\tesseract50.dll", "x64\leptonica-1.82.0.dll")) {
    $tam = Join-Path $Klasor $d
    if (-not (Test-Path $tam)) { Kotu "$d  -> dosya yok, test edilemedi"; continue }
    $h = [PlakaTani.K32Tani]::LoadLibraryEx($tam, [IntPtr]::Zero, 0x00000008)
    if ($h -ne [IntPtr]::Zero) { Iyi "$d yuklendi" }
    else {
        $e = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $aciklama = ([ComponentModel.Win32Exception]$e).Message
        Kotu "$d YUKLENEMEDI  hata=$e ($aciklama)"
        $sorunlar.Add("$d yuklenemiyor (hata ${e}: $aciklama)")
        if ($e -eq 126) { $sorunlar.Add(" -> hata 126 = bagimlilik eksik; neredeyse her zaman VC++ Runtime (x64)") }
        if ($e -eq 5)   { $sorunlar.Add(" -> hata 5 = erisim reddedildi; antivirus engelliyor olabilir") }
        if ($e -eq 193) { $sorunlar.Add(" -> hata 193 = mimari uyusmazligi (32/64 bit)") }
    }
}

# ---------------------------------------------------------------------
Baslik "6) ANTIVIRUS / KARANTINA"
try {
    $tehdit = Get-MpThreatDetection -ErrorAction SilentlyContinue | Sort-Object InitialDetectionTime -Descending | Select-Object -First 10
    if ($tehdit) {
        foreach ($t in $tehdit) {
            Bilgi ("{0} | {1} | {2}" -f $t.InitialDetectionTime, $t.ThreatID, ($t.Resources -join ","))
        }
        $sorunlar.Add("Defender'da tehdit kaydi var - onnxruntime/tesseract karantinaya alinmis olabilir (yukaridaki listeye bakin)")
    } else { Iyi "Defender tehdit kaydi yok" }
} catch { Bilgi "Defender sorgulanamadi (Get-MpThreatDetection yok)" }

# ---------------------------------------------------------------------
Baslik "7) UYGULAMA LOGUNDAKI SON MOTOR DURUMU"
$log = Join-Path $ModelKlasoru "log.txt"
if (Test-Path $log) {
    $satirlar = Get-Content $log -Tail 4000 | Where-Object { $_ -match "Lokal motor|ONNX .* yuklenemedi|Tesseract baslatilamadi|ORTAM TANISI|IC SEBEP" }
    if ($satirlar) { $satirlar | Select-Object -Last 15 | ForEach-Object { Bilgi $_ } }
    else { Bilgi "Logda motor satiri bulunamadi." }
} else { Bilgi "Log yok: $log" }

# ---------------------------------------------------------------------
Baslik "SONUC"
if ($sorunlar.Count -eq 0) {
    Write-Host "  Bu makinede eksik bir bilesen BULUNAMADI. Motor su an yuklenebiliyor." -ForegroundColor Green
    Write-Host "  Sorun devam ediyorsa: uygulamayi yeniden baslatip C:\Otopark\log.txt icindeki" -ForegroundColor Yellow
    Write-Host "  'Lokal motor:' satirina bakin (KULLANILABILIR=True olmali)." -ForegroundColor Yellow
} else {
    Write-Host "  BULUNAN SORUNLAR:" -ForegroundColor Red
    $sorunlar | ForEach-Object { Write-Host "   - $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "  EN OLASI COZUM: Microsoft Visual C++ 2015-2022 Redistributable (x64) kurun:" -ForegroundColor Yellow
    Write-Host "     https://aka.ms/vs/17/release/vc_redist.x64.exe" -ForegroundColor Yellow
    Write-Host "  Kurduktan sonra bilgisayari yeniden baslatin ve Otopark.Client'i acin." -ForegroundColor Yellow
}
Write-Host ""
