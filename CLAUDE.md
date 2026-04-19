# Otopark - Kapali Otopark Yonetim Sistemi

## Proje Ozeti

Kapali otopark tesislerinde arac giris/cikis yonetimi icin gelistirilmis bir WPF masaustu uygulamasi. Sistem, kamera goruntulerinden PlateRecognizer Cloud API ile otomatik plaka tanima (ALPR) yaparak arac girislerini/cikislarini kaydeder, bariyer kontrolu saglar ve personel paneli uzerinden otopark dolulugunu takip eder.

## Teknoloji Yigini

- **Platform:** .NET 8.0, WPF (Windows)
- **Dil:** C#
- **Mimari:** MVVM (CommunityToolkit.Mvvm 8.4.0)
- **DI:** Microsoft.Extensions.DependencyInjection
- **Plaka Tanima:** PlateRecognizer Cloud API (%95-99 dogruluk)
- **JSON:** Newtonsoft.Json 13.0.1

## Proje Yapisi

```
Otopark.sln
├── Otopark.Client/        # WPF masaustu uygulamasi (UI katmani)
│   ├── Views/             # XAML gorunumleri (Login, PersonnelDashboard)
│   ├── Services/          # AppConfig, AppConfigHelper, BarrierService
│   ├── Helpers/           # PlateRecognitionHelpers (API client, stabilizasyon, plaka kurallari)
│   ├── Converters/        # BoolToVisibilityConverter, BoolToColorConverter
│   └── Assets/            # Logo gorselleri
├── Otopark.Core/          # Is mantigi ve ViewModel katmani
│   ├── Models/            # Domain modelleri
│   ├── Session/           # Statik kullanici oturumu (UserSession)
│   └── ViewModel/         # MVVM ViewModel'ler (Login, Dashboard, Main)
└── Otopark.Api/           # Backend API istemci katmani
    ├── Services/          # AuthApiService, VehicleParkApiService, VehicleDefinitionApiService, ZoneApiService
    └── OpenAPIs/          # swagger.json
```

## Derleme ve Calistirma

```bash
dotnet build Otopark.sln
dotnet run --project Otopark.Client
```

Gereksinimler:
- .NET 8.0 SDK
- Windows (WPF gerektigi icin)
- Visual Studio 2022 (v17.10+)

## Temel Is Akislari

### Giris Ekrani
- POST /Login/LoginControl -> UserSession (UserId, CompanyId, UserName)
- Zone/GetZones API'sinden bolge listesi yuklenir (companyId=2, zoneClassId=424)
- Kullanici bolge secebilir (istege bagli)
- Basarili giris sonrasi tam ekran PersonnelDashboard'a gecilir

### Plaka Tanima (PlateRecognizer Cloud API)
1. `C:\Otopark\EntryCaptures\` (giris) ve `C:\Otopark\ExitCaptures\` (cikis) klasorleri izlenir
2. Yeni goruntu tespit edildiginde PlateRecognizer API'ye gonderilir
3. Donen plaka metni normalize edilir ve Turk plaka formatina (%70+ guven) dogrulanir
4. DuplicateSuppressor ile ayni plaka 8sn icinde tekrar gonderilmez
5. Taninan plaka ekranda gosterilir, personel onayini bekler

### Arac Giris
1. Plaka otomatik veya manuel olarak tanindiktan sonra ekranda gosterilir
2. Personel "Onayla" veya "Fis Bas/Onayla" butonuna basar
3. POST /VehiclePark/AddVehicleParkEntry API'ye giris kaydi gonderilir
4. Basarili ise: tabloya eklenir, KPI guncellenir, giris bariyeri otomatik acilir
5. Basarisiz ise: toast ile hata mesaji gosterilir

### Arac Cikis
1. Cikis kamerasindaki plaka tanindiktan sonra ekranda gosterilir
2. Personel "Onayla" butonuna basar
3. POST /VehicleDefinition/GetVehicleByPlate ile arac sorgulanir
4. `credit <= 0` (borcu yok): POST /VehiclePark/AddVehicleExit ile cikis kaydedilir, bariyer acilir
5. `credit > 0` (borcu var): "Kiosk cihazinda odemenizi gerceklestiriniz" uyarisi verilir

### Bariyer Kontrolu
- HTTP GET + Basic/Digest Auth ile IP tabanli bariyer kontrolculeri
- Giris bariyeri: appsettings.json > Barrier:EntryCommandUrl
- Cikis bariyeri: appsettings.json > Barrier:ExitCommandUrl
- Manuel kontrol: Ust bardaki Bar. G (giris) / C (cikis) butonlari
- Otomatik: Giris onayinda giris bariyeri, cikis onayinda cikis bariyeri acilir
- Basari/hata durumu toast ile bildirilir

### Filtreleme Sistemi
- **Plaka Arama:** Plaka metnine gore filtreleme
- **Durum Filtresi:** Onaylilar (aktif parklar) / Onaysizlar (cikis yapilmis) / Iptaller / Hepsini
- **Zaman Filtresi:** Bu Mesai (08:00'den itibaren) / Gun / Hafta / Ay
- Tum veriler `_allVehicles` listesinde tutulur, `VehicleList` filtrelenmis gorunumdur

### KPI (Ust Bar)
- **Otopark Kapasitesi:** Zone/GetZones API'sinden secilen bolgenin `capacity` alani
- **Arac Park Sayisi:** Girisi yapilip cikisi yapilmamis araclarin sayisi
- **Bos Park Sayisi:** Kapasite - Arac Park Sayisi
- Giris/cikis her onayda otomatik guncellenir

## API Endpointleri

| Endpoint | Metod | Aciklama |
|---|---|---|
| /Login/LoginControl | POST | Kullanici girisi |
| /Zone/GetZones | POST | Bolge listesi (companyId, zoneClassId) |
| /VehiclePark/AddVehicleParkEntry | POST | Arac giris kaydi |
| /VehiclePark/AddVehicleExit | POST | Arac cikis kaydi |
| /VehicleDefinition/GetVehicleByPlate | POST | Plakaya gore arac sorgulama (borc kontrolu) |

Backend API: `http://web.belsoft.com.tr:221/`

## Dis Entegrasyonlar

- **Backend API:** http://web.belsoft.com.tr:221/ (ParkomatApp)
- **PlateRecognizer API:** https://api.platerecognizer.com/v1/plate-reader/ (plaka tanima)
- **Bariyer Kontrolu:** HTTP GET ile IP tabanli kontrolculer
- **Kamera Sistemi:** Yerel klasor izleme (EntryCaptures / ExitCaptures)

## Konfigurasyon

Tum yapilandirma `Otopark.Client/appsettings.json` icerisindedir:

```json
{
  "Camera": {
    "Username": "admin",
    "Password": "admin",
    "ImagePool": "C:\\Otopark\\Captures\\",
    "FileExtension": "jpg",
    "RefreshInterval": 1000
  },
  "Barrier": {
    "EntryCommandUrl": "http://192.168.108.114/command/...",
    "ExitCommandUrl": "http://192.168.108.113/command/...",
    "DelayMs": 100
  },
  "Parking": {
    "Capacity": 384,
    "BolgeId": 342
  }
}
```

## Klasor Yapisi (Calisma Zamani)

```
C:\Otopark\
├── EntryCaptures\     # Giris kamerasi goruntuler (otomatik izlenir)
├── ExitCaptures\      # Cikis kamerasi goruntuler (otomatik izlenir)
├── EntryShots\        # Manuel giris yakalama kayitlari
├── ExitShots\         # Manuel cikis yakalama kayitlari
└── log.txt            # Uygulama loglari
```

## UI Yapisi

- **Login Ekrani:** Kullanici adi, sifre, firma kodu, bolge secimi
- **Dashboard (tam ekran):**
  - **Ust Bar:** KPI kartlari (hasilat, kapasite, arac sayisi, bos park), kamera/bariyer kontrolleri, kullanici bilgisi + bolge
  - **Sol Panel:** Arac tablosu (plaka, giris/cikis zamani, plaka gorselleri, borc bilgisi), filtreler
  - **Sag Panel:** Arac Giris bolumu (plaka, kamera goruntusu, son 2 gorsel, onayla butonlari) + Arac Cikis bolumu (ayni yapi, kirmizi tema)
  - **Alt Bar:** Kacirmalardan iceri al (plaka girisi + buton)
  - **Toast Bildirim:** Basarili (yesil) / basarisiz (kirmizi) islemler icin overlay bildirim

## Kodlama Kurallari

- Turkce UI metinleri kullanilir (XAML ve code-behind)
- MVVM pattern'i takip edilir; ViewModel'ler CommunityToolkit.Mvvm ile olusturulur
- Async/await pattern'i tum I/O islemlerinde kullanilir
- API response'lari her zaman errors[] kontrolu yapilir (null + count > 0)
- HTTP hatalari (400, 500 vb.) exception firlatmaz, response body parse edilir
- Toast ile kullaniciya basari/hata bildirimi yapilir
- Hata loglari C:\Otopark\log.txt dosyasina yazilir
