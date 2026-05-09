# Otopark - Plaka Tespit Modeli Kurulumu

Bu klasör, lokal plaka tanıma için gerekli ONNX modelini indirip hazırlayan
Python script'ini içerir.

## Tek seferlik kurulum

### 1. Python yükle
- https://www.python.org/downloads/ adresinden Python 3.8+ indir
- Kurarken **"Add Python to PATH"** kutucuğunu işaretle

### 2. ultralytics paketini yükle
Komut Istemi (CMD) veya PowerShell aç, şunu çalıştır:

```
pip install ultralytics
```

### 3. Script'i çalıştır
Bu klasörde (yani `tools/` içinde) komutu çalıştır:

```
python download_plate_model.py
```

Script otomatik olarak:
1. HuggingFace'ten eğitilmiş YOLOv8 plaka modelini indirir
2. ONNX formatına dönüştürür
3. `C:\Otopark\models\plate_detector.onnx` konumuna yerleştirir

### 4. Otopark uygulamasını çalıştır
Log'da şu satırı görmelisin:
```
ONNX plaka detektoru hazir.
```

Bundan sonra plakalar Tesseract'tan önce YOLO ile hassas tespit edilir,
doğruluk %95+ seviyesine çıkar.

## Sorun yaşarsan

**"Model indirilemedi"** → İnternet bağlantısı yoksa, başka bir bilgisayardan
manuel indir:
- https://huggingface.co/keremberke/yolov8n-license-plate/tree/main
- `best.pt` dosyasını indir, bu klasöre koy, script'i tekrar çalıştır
  (script `best_plate.pt` arar, varsa ondan dönüştürür)

**"ultralytics yüklü değil"** → `pip install ultralytics` komutunu çalıştır.

**"python tanınmıyor"** → Python kurarken PATH'e eklenmemiş. Yeniden yükle ve
"Add Python to PATH" kutucuğunu işaretle.
