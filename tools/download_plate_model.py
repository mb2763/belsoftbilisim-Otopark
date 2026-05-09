"""
Otopark - Plaka Tespit Modelini Indir ve ONNX'e Donustur
=========================================================
Bu script HuggingFace'ten egitilmis bir YOLOv8 plaka tespit modelini indirir
ve Otopark uygulamasinin kullanabilecegi ONNX formatina donusturur.

Tek seferlik calistirilir, sonuc dosyasi:
  C:\\Otopark\\models\\plate_detector.onnx

Gereksinimler:
  - Python 3.8+
  - pip install ultralytics

Calistirma:
  python download_plate_model.py
"""

import os
import sys
import shutil
import urllib.request

# Hedef klasor
TARGET_DIR = r"C:\Otopark\models"
TARGET_FILE = os.path.join(TARGET_DIR, "plate_detector.onnx")

# Mumkun olabilecek model URL'leri
MODEL_URLS = [
    "https://huggingface.co/keremberke/yolov8n-license-plate/resolve/main/best.pt",
    "https://huggingface.co/morsetechlab/yolov11-license-plate-detection/resolve/main/yolov11n-license-plate-detection.pt",
]
TEMP_PT = "best_plate.pt"

# Yerel olarak hazir bulunabilecek dosyalar (manuel indirme icin)
LOCAL_CANDIDATES = ["best_plate.pt", "best.pt", "yolov8n-license-plate.pt", "yolov11n-license-plate-detection.pt"]


def try_download(url, dest):
    """User-Agent ile indirmeyi dene."""
    try:
        req = urllib.request.Request(url, headers={
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
        })
        with urllib.request.urlopen(req, timeout=60) as resp, open(dest, "wb") as f:
            shutil.copyfileobj(resp, f)
        return True
    except Exception as e:
        print(f"      Hata: {e}")
        return False


def find_local_pt():
    """Script'in yanindaki klasorde manuel indirilmis .pt arar."""
    for name in LOCAL_CANDIDATES:
        if os.path.exists(name):
            size_mb = os.path.getsize(name) / (1024 * 1024)
            if size_mb > 1:  # gecerli bir model dosyasi olabilir
                return name
    return None


def main():
    print("=" * 60)
    print("Otopark - Plaka Tespit Modeli Hazirlanyor")
    print("=" * 60)

    # 1. Hedef klasoru olustur
    os.makedirs(TARGET_DIR, exist_ok=True)
    print(f"\n[1/4] Hedef klasor: {TARGET_DIR}")

    # 2a. Once yerel manuel indirilmis dosya var mi bak
    local_pt = find_local_pt()
    if local_pt:
        print(f"\n[2/4] Yerel model dosyasi bulundu: {local_pt}")
        size_mb = os.path.getsize(local_pt) / (1024 * 1024)
        print(f"      Boyut: {size_mb:.1f} MB")
        TEMP_PT_USE = local_pt
    else:
        # 2b. URL'leri sirayla dene
        print(f"\n[2/4] Model indiriliyor (User-Agent ile)...")
        TEMP_PT_USE = TEMP_PT
        downloaded = False
        for url in MODEL_URLS:
            print(f"      Deneme: {url}")
            if try_download(url, TEMP_PT_USE):
                size_mb = os.path.getsize(TEMP_PT_USE) / (1024 * 1024)
                if size_mb > 1:
                    print(f"      Indirildi ({size_mb:.1f} MB)")
                    downloaded = True
                    break
                else:
                    print(f"      Bos dosya geldi ({size_mb:.2f} MB), bir sonraki url deneniyor")
                    os.remove(TEMP_PT_USE)

        if not downloaded:
            print("\n[HATA] Hicbir URL'den model indirilemedi.")
            print("\n>>> MANUEL INDIRME YONERGESI <<<")
            print("1. Tarayicinizla su sayfayi acin:")
            print("   https://huggingface.co/keremberke/yolov8n-license-plate/tree/main")
            print("2. 'best.pt' dosyasini ustune tiklayip 'Download' diyerek indirin.")
            print(f"3. Indirilen dosyayi BU klasore koyun: {os.getcwd()}")
            print("4. Bu scripti tekrar calistirin (yerel dosyayi otomatik bulacak).")
            print("\nNot: HuggingFace icin giris yapmaniz gerekebilir (ucretsiz).")
            sys.exit(1)

    # 3. Ultralytics yukle
    try:
        from ultralytics import YOLO
    except ImportError:
        print("\n[HATA] 'ultralytics' yuklu degil.")
        print("Su komutu calistirin:")
        print("  pip install ultralytics")
        sys.exit(1)

    # 4. ONNX'e donustur
    print(f"\n[3/4] ONNX formatina donusturuluyor...")
    model = YOLO(TEMP_PT_USE)
    output_path = model.export(format="onnx", imgsz=640, opset=12)
    print(f"      Olusturuldu: {output_path}")

    # 5. Hedef konuma kopyala
    print(f"\n[4/4] Kopyalaniyor: {TARGET_FILE}")
    shutil.copy(output_path, TARGET_FILE)

    # Gecici dosyalari temizle (sadece ondan sonra olusturulanlari, yerel manuel indiremiyi degil)
    if TEMP_PT_USE == TEMP_PT and os.path.exists(TEMP_PT):
        try: os.remove(TEMP_PT)
        except Exception: pass
    if os.path.exists(output_path) and os.path.abspath(output_path) != os.path.abspath(TARGET_FILE):
        try: os.remove(output_path)
        except Exception: pass

    print("\n" + "=" * 60)
    print(f"BASARILI! Model hazir: {TARGET_FILE}")
    print(f"Boyut: {os.path.getsize(TARGET_FILE) / (1024 * 1024):.1f} MB")
    print("=" * 60)
    print("\nArtik Otopark uygulamasini calistirabilirsiniz.")
    print("Uygulamayi acinca log'da su satiri gormelisiniz:")
    print("  'ONNX plaka detektoru hazir.'")


if __name__ == "__main__":
    main()
