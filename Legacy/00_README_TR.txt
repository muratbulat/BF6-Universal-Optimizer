BF6 UNIVERSAL OPTIMIZER v1.4.0 - KULLANIM KILAVUZU
===================================================

AMAC
----
Windows 10/11 ve PowerShell 5.1 icin, farkli donanimlarda calisabilen, BF6
oyun profili optimizasyonu. Varsayilan hedef rekabetci FPS/input latency;
her bilgisayarda FPS artisi garanti degildir. Once/sonra oyun ici test yapin.

KURULUM VE CALISTIRMA
---------------------
1) ZIP icerigini normal bir klasore cikarin. ZIP icinden dogrudan calistirmayin.
2) Battlefield 6'yi bir kez acip kapatin.
3) START_HERE.cmd > 1 (Analyze): algilanan GPU, RAM, ekran ve profili gorun.
4) START_HERE.cmd > 2 (Apply Auto): otomatik plan uygula; UAC GEREKMEZ.
5) START_HERE.cmd > 3 (Status): son basarili uygulamayi dogrula.
6) Gerektiginde 4 (son basarili snapshot) veya 5 (ilk baseline) ile geri donun.

YEDEKLER VE RAPORLAR
--------------------
Varsayilan olarak paketin yanindaki Backups/ ve Reports/ klasorleri kullanilir:
  Backups\Machine_<ID>\OriginalBaseline
  Backups\Machine_<ID>\Snapshots\<benzersiz zaman>
  Reports\Machine_<ID>\History
  Reports\Machine_<ID>\Latest_Apply_Error.json
Paket klasoru yazmaya izin vermiyorsa veri konumu otomatik olarak
%%LOCALAPPDATA%%\BF6UniversalOptimizer olur. 6 ve 7 numarali menu
secenekleri gercek klasoru acar. Analyze hicbir rapor/yedek olusturmaz.
Apply basarisiz olursa asama bilgisi ve hata nedeni JSON raporuna kaydedilir.

GUVENLIK VE EVRENSEL UYUMLULUK
-----------------------------
- Yalnizca BF6 profilinde zaten bulunan taninan grafik ayarlari degistirilir.
- Bulunmayan anahtarlar ve bilinmeyen enum degerleri zorlanmaz.
- NVIDIA / AMD / Intel icin uygun dusuk gecikme anahtari, mevcutsa duzenlenir.
- DLSS / FSR / XeSS enum secimi korunur; yanlis kalite modu zorlanmaz.
- 240 Hz orneginde otomatik FPS limiti 237'dir. VRR ve FPS stabilitesine
  gore kullanici bunu manuel degistirebilir: 08_COMMANDS.txt
- Ekran yenileme hizi algilanamazsa mevcut FPS limiti korunur.
- Kullanici bazinda Game Mode acilir, Game DVR / capture kapatilir
  (Windows politikasi izin verdigi olcude).
- HAGS degistirilmez; HAGS guncel Windows Graphics ayarlarindan yonetilebilir.
- Yonetici yetkisine gecilmez; farkli admin hesabinin HKCU/Belgeler klasoru
  yanlislikla hedef alinmaz.
- Secure Boot, TPM, VBS, HVCI, CPU OC/PBO, GPU power, HPET, NIC, servis
  temizligi, power plan degisiklikleri YOKTUR.
- Apply oncesi tam yedek, SHA256 kontrolu, uygulama sonrasi iki asamali
  dogrulama, hata durumunda otomatik rollback denemesi vardir.
- Read-only BF6 profil dosyalarini otomatik zorlamaz.

v1.4.0 DEGISIKLIKLERI
--------------------
* Gereksiz UAC ve zorunlu HAGS kaldirildi.
* BF6 surumu farkliliginda olmayan anahtarlara fatal hata verilmez.
* Preflight dahil tum Apply hatalari kalici Latest_Apply_Error.json uretebilir.
* UTF8 / UTF16 / UTF32 BOM, ANSI fallback, CRLF ve SHA256 korumasi.
* Dosya yazma icin replace-oncelikli yontem ve benzersiz temp dosyasi.
* Status son basarili Apply sirasinda kaydedilen anahtarlari kontrol eder;
  profil anahtari silindiyse bile yanlis basari vermez.
* Snapshot ismi cakismalari onlendi; pointer yalnizca basarili Apply'da artar.
* Salt okunur paket dizinleri icin kullanici veri klasorune gecis eklendi.

SINIRLAMALAR
------------
BF6 guncellemesi profil semasini degistirebilir; taninmayan profiller
bilerek atlanir. Steam/EA cloud sync oyun ayarlarini sonradan ezebilir.
Kurulum ortami Windows olmadigi icin gercek Windows/BF6 calistirma testi
bu pakette yapilmadi; kod statik kontrollerden gecirilmektedir.
Raporlar kullanici dizini ve cihaz ayrintilari icerebilir: paylasmadan once inceleyin.
