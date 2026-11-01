# BF6 Universal Optimizer 2.0

Windows 10/11 x64 için tek dosyalık, kurulum gerektirmeyen oyun sistemi analizörü ve Battlefield 6 optimizasyon aracı.

Yazar: Murat Bulat

## Kullanım

1. `Release\BF6Optimizer.exe` dosyasını istediğiniz klasöre kopyalayın.
2. Çift tıklayın.

.NET, Visual C++ paketi, PowerShell modülü, sürücü, hizmet veya yönetici izni gerekmez. İnternet bağlantısı gerekmez. Uygulama arka planda çalışmaz; pencereyi kapatınca tamamen kapanır.

## Özellikler

**Battlefield 6**
- Otomatik, Rekabetçi, Dengeli, Kalite ve Özel modlar
- Değiştirilecek her ayar için önce/sonra önizlemesi ve amacı
- Yedek, yazma, iki aşamalı doğrulama ve hata durumunda otomatik geri alma
- Özgün temel yedek, anlık görüntüler ve v1.4.0 yedeklerini geri yükleme
- Son başarılı uygulamaya göre durum doğrulaması

**Windows oyun sistemi analizi**
- Bellek, CPU, GPU, ekran, Windows ayarları, depolama, ağ, işlemler ve BIOS/UEFI bilgileri
- Oyun Yapılandırma Sağlığı skoru ve ayrı Değerlendirme Kapsamı
- Canlı izleme ve performans oturumları
- HTML, JSON ve metin raporları; dışa aktarmadan önce gizlilik incelemesi

## Veri konumu

Varsayılan konum `%LOCALAPPDATA%\BF6UniversalOptimizer` altındaki `Data`, `Backups`, `Reports`, `Logs`, `Profiles` ve `Settings` klasörleridir. Ayarlar sayfasından isteğe bağlı taşınabilir veri modu açılabilir; bu mod verileri EXE'nin yanındaki `BF6OptimizerData` klasöründe tutar. EXE klasörü yazılabilir değilse normal moda dönülür ve uyarı gösterilir.

SQLite kullanılmadı. Veriler küçük, kayıt tabanlı ve sorgu gerektirmediği için sürümlü JSON dosyaları yeterlidir; bu tercih yerel bir veritabanı kütüphanesi bağımlılığını da ortadan kaldırır.

## Güvenlik sınırları

Bu uygulama şunları asla yapmaz: Secure Boot, TPM, VBS, HVCI, Defender veya güvenlik duvarını değiştirmek; HAGS, HPET, hizmetler, ağ bağdaştırıcısı ayarları, BIOS/UEFI, CPU/GPU güç sınırları veya sürücü profillerine yazmak; oyun dosyalarına veya sürecine müdahale etmek; hile koruma sistemini atlatmak.

Yazma işlemleri yalnızca şunlarla sınırlıdır: BF6 profil dosyalarındaki doğrulanmış ayarlar, HKCU altında dört Windows oyun tercihi ve kullanıcıya ait başlangıç girdileri. Hepsi onay ister ve geri alınabilir.

Elle değerlendirme gerektiren ayarlar (XMP/EXPO, PBO, Resizable BAR, VRR, sürücü paneli) "Elle doğrulayın" olarak gösterilir. Uygulama bunları tahmin etmez. Skorlar ve seviyeler tahmindir, kıyaslama sonucu değildir; FPS artışı garanti edilmez.

## Bilinen sınırlamalar

- Yenileme hızı otomatik değiştirilmez; uygulama Windows ayar sayfasını açar.
- FPS ve kare süresi ölçülmez: oyuna müdahale etmeyen desteklenen bir kaynak yoktur.
- CPU sıcaklığı gösterilmez. GPU sıcaklığı yalnızca NVIDIA sürücüsünün sağladığı NVML varsa görünür.
- Wi-Fi sinyal kalitesi ve bağdaştırıcı güç ayarları okunmaz.
- Kalite seviyelerinin yazılabilir değerleri yalnızca v1.4.0'ın kullandığı değerlerle sınırlıdır; sayısal seviyelerin oyun içi adları doğrulanmadığı için numara olarak gösterilir.
- BF6 güncellemesi profil yapısını değiştirebilir; tanınmayan profiller bilerek atlanır. Steam Cloud ayarları sonradan geri yazabilir.
- İlk özgün temel yedek, uygulamanın ilk Uygula anındaki durumu saklar. Daha önce başka bir araç ayarları değiştirdiyse bu durum fabrika ayarı değildir.

## Geliştirme

Gereken: .NET 10 SDK.

```powershell
dotnet build BF6Optimizer.sln
dotnet test tests\BF6Optimizer.Tests
.\build-release.ps1
```

`build-release.ps1` derler, testleri çalıştırır, tek dosya yayımlar, çıktıyı doğrular, `Release\BF6Optimizer.exe` dosyasını üretir ve `artifacts\release-manifest.json` yazar.

Yapı: `src\BF6Optimizer.Core` (etki alanı, Windows bağımlılığı yok), `src\BF6Optimizer.Infrastructure` (Windows erişimi, depolama), `src\BF6Optimizer.App` (WPF), `tests\BF6Optimizer.Tests`. Özgün PowerShell paketi değiştirilmeden `Legacy\` altındadır. Davranış karşılaştırması: [docs/LEGACY_BEHAVIOR.md](docs/LEGACY_BEHAVIOR.md). Temiz makine doğrulaması: [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md).

Debug derlemesi `--simulate-hardware` ile benzetilmiş donanım gösterebilir; bu kod Release derlemesine dahil değildir ve etkinken ekranda uyarı görünür.
