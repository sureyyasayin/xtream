# Xtream STRM — Jellyfin eklentisi

Kendi Xtream hesabınızdaki film, dizi ve canlı TV kanallarını Jellyfin'e ekler. Filmler ve diziler, seçtiğiniz kategorilerden `.strm` kısayolları olarak medya klasörlerine yazılır. Dosyalar indirilmez; oynatınca doğrudan Xtream sunucusundan akar. Jellyfin bunları normal film/dizi gibi tanır (afiş, özet, izlenme durumu vb.). Canlı kanallar Jellyfin'in **Canlı TV** bölümüne yayın akışıyla (EPG) birlikte eklenir.

**Eklenti hiçbir kanal, film ya da içerik listesi içermez ve dağıtmaz.** Yalnızca sizin girdiğiniz Xtream hesabını Jellyfin'e bağlar; hesabın ve içeriğin kullanımından hesap sahibi sorumludur.

## Gereksinimler

- **Jellyfin 12.4** veya üzeri (Docker ya da doğrudan kurulum).
- Bir Xtream (Xtream Codes API) hesabı: sunucu adresi, kullanıcı adı ve şifre.

## Kurulum

1. Jellyfin → **Kontrol Paneli → Eklentiler → Depolar → +** ile şu adresi ekleyin:

   ```text
   https://raw.githubusercontent.com/sureyyasayin/xtream/main/manifest.json
   ```

2. **Katalog** sekmesinden **Xtream STRM** eklentisini kurun ve Jellyfin'i yeniden başlatın.

Yeni sürümler aynı depodan gelir; Jellyfin eklentiyi kendiliğinden günceller (yeniden başlatınca devreye girer).

## Kullanım

1. **Kontrol Paneli → Eklentiler → Xtream STRM** sayfasını açın.
2. Xtream adresini (ör. `http://sunucu.com:8080`), kullanıcı adını ve şifreyi girip **Kaydet ve bağlantıyı test et**'e basın.
3. Açılan listelerden istediğiniz film, dizi ve canlı TV kategorilerini işaretleyin. Seçim yapılmayan türden içerik alınmaz.
4. Film ve dizi klasörlerinin, Jellyfin'deki film ve dizi kütüphanelerinizin klasörleri olduğunu kontrol edin. Varsayılanlar `/data/movies` ve `/data/tvshows`'dur; kütüphaneleriniz başka klasördeyse bunları değiştirin (Docker'da container içindeki yolu yazın).
5. **Kaydet ve şimdi senkronize et**'e basın. İlerleme **Zamanlanmış Görevler** altında görünür.

Senkronizasyon her gece 04:00'te kendiliğinden tekrarlanır; saat Zamanlanmış Görevler'den değiştirilebilir.

Bilgileriniz yalnızca kendi Jellyfin sunucunuzda saklanır. Eklenti yalnızca sizin girdiğiniz Xtream sunucusuyla konuşur, başka hiçbir yere veri göndermez.

## Şifre gizliliği

Xtream yayın adreslerinin içinde kullanıcı adı ve şifre bulunur. Eklenti varsayılan olarak bunları `.strm` dosyalarına yazmaz; dosyalara eklentinin imzalı adresi yazılır (`http://jellyfin-adresi/XtreamStrm/Play/...`). Böylece şifre dosyalarda, Jellyfin arayüzünde ve kütüphane bilgilerinde görünmez; hesap ya da adres değişince dosyaların yeniden yazılması da gerekmez.

- **Yönlendirme (varsayılan):** Oynatma başlarken eklenti asıl adrese yönlendirir; film doğrudan sağlayıcıdan akar, sunucuya ek yük binmez. Teknik bilgisi olan bir kütüphane kullanıcısı oynatma sırasında asıl adresi yine de görebilir.
- **Sunucu üzerinden aktarma (isteğe bağlı):** Ayarlardan açılırsa film ve diziler sunucu üzerinden aktarılır, şifre hiçbir cihaza ulaşmaz. Sunucuyu güvenmediğiniz kişilerle paylaşıyorsanız açın; dışarıdan izlerken sunucunuzun internet bağlantısını kullanır.
- **Jellyfin adresi:** Oynatıcıların Jellyfin'e ulaştığı adres gerekir. Ayarlarda boş bırakılırsa `JELLYFIN_PublishedServerUrl` kullanılır; ikisi de yoksa şifre gizlenemez ve senkronizasyon özeti bunu bildirir.
- Her adres sunucuya özel gizli bir anahtarla imzalıdır; imzasız ya da değiştirilmiş adres reddedilir.
- Canlı TV kanal listesi sunucuda ayrı bir dosyada durur ve kullanıcılara gösterilmez.

## Ayarlar hakkında

- **İstekler arası bekleme:** Çoğu Xtream sağlayıcısı kısa sürede çok istek gelince bir süre yanıt vermez. Varsayılan 2500 ms çoğu sağlayıcı için güvenlidir; sağlayıcınız izin veriyorsa düşürebilirsiniz.
- **Aynı anda izlenebilecek kanal sayısı:** Hesabınızın bağlantı sınırını girin (çoğu hesapta 1). 0 sınırsız demektir.
- **Aynı kanalın farklı kaliteleri:** Birçok sağlayıcı aynı kanalı HD, UHD, 4K gibi ayrı kategorilerde verir. Canlı TV'de her türün tek bir kalite kategorisini seçerseniz kanal listesi ve kumandayla kanal geçişi daha düzenli olur.

## Davranış

- Film: `Film Adı (Yıl) [tmdbid-123]/Film Adı (Yıl).strm`
- Dizi: `Dizi Adı (Yıl)/Season 01/Dizi Adı (Yıl) S01E01.strm`
- Canlı TV: seçilen kategorilerdeki kanallar Jellyfin'in **Canlı TV** bölümüne "Xtream" adlı tuner olarak eklenir; yayın akışı (EPG) yalnızca bu kanallara süzülerek bağlanır. Canlı TV kapatılırsa tuner kaldırılır.
- Kategoriler korunur: Jellyfin kanal kategorisi saklamadığı için kanallar Xtream kategorilerinin sırasıyla dizilir ve her kategori kendi numara bloğunu alır (ör. ULUSAL 1–31, HABER 101–145, SPOR 201–234). Jellyfin'in kanal listesi ve rehberi numaraya göre sıralandığından kategoriler bir arada görünür; numaradan da kategori anlaşılır.
- **Xtream kutuları:** Jellyfin'deki Filmler, Diziler ve Canlı TV olduğu gibi kalır; ana sayfada (Medyam) ayrıca **Xtream Canlı TV**, **Xtream Filmler** ve **Xtream Diziler** kutuları görünür. İçlerinde Xtream kategorileri Xtream'deki sırayla klasör olarak durur (diziler: kategori → dizi → sezon → bölüm). Veriler senkronizasyonun yazdığı katalogdan okunur, gezinirken Xtream'e istek gitmez. Her kutu ayarlardan ayrı ayrı açılıp kapatılabilir.
- Xtream Filmler / Diziler'de bir film, dizi ya da bölüme tıklanınca kütüphanedeki kartı açılır (afiş, özet, izlenme durumu ortak; LG webOS uygulaması ve tarayıcı). Bu da ayarlardan kapatılabilir; kapalıyken kutudaki kopya açılır ve izlenme durumu ayrı tutulur.
- Xtream hesabı ya da adresi değişince Jellyfin'de kalan eski (artık açılmayan) kanal kayıtları kendiliğinden temizlenir.
- Kumandayla kanal değiştirme (LG webOS Jellyfin uygulaması ve tarayıcı): canlı kanal izlerken **CH+ / CH-** ve alt çubuk kapalıyken **Yukarı / Aşağı** sonraki / önceki kanala geçer; **rakam** tuşları kanal numarasına gider; **Yeşil** tuş bir önceki kanala döner; **Kırmızı / Mavi** önceki / sonraki kategorinin ilk kanalına atlar. Art arda basınca ekranda kanal adı ve kategorisi görünür, tuş bırakılınca son seçilen kanal açılır. Tek bağlantılı hesapta da çalışır: aynı cihaz yeni kanal isteyince önceki yayını kapatılır (başka cihazların yayınlarına dokunulmaz). Eklenti bunun için Jellyfin web arayüzüne küçük bir betik ekler (Jellyfin dosyalarına dokunmaz); ayarlardan kapatılabilir. Samsung (Tizen), Android TV ve diğer yerel uygulamalar arayüzü kendi içinde taşıdığı için bu özellik orada çalışmaz; canlı TV'nin kendisi her uygulamada çalışır.
- Bir film sağlayıcıda boş geliyorsa (dosyası bozuk ya da kaldırılmış), aynı adlı ve aynı yıllı başka bir kopyası (ör. 4K yerine HD kategorisindeki) kendiliğinden açılır.
- Aynı isimde bir klasör zaten varsa (kendi dosyalarınız) o içerik atlanır; mevcut dosyalarınıza dokunulmaz.
- Xtream listesinden kalkan ya da seçimi kaldırılan kategorilere ait kısayollar silinir (ayarlardan kapatılabilir). Yalnızca eklentinin kendi oluşturduğu dosyalar silinir.
- Sunucuya ulaşılamazsa hiçbir dosya silinmez.
- Değişmeyen dizilerin bölüm listesi tekrar indirilmez.

Ayar sayfası yalnızca Türkçedir.

## Derleme

Docker yeterlidir, .NET SDK kurmak gerekmez:

```sh
cd Jellyfin.Plugin.XtreamStrm
sh build.sh        # çıktı: out/Jellyfin.Plugin.XtreamStrm.dll
```

## Lisans

[MIT](LICENSE) — herkes ücretsiz kullanabilir, değiştirebilir ve paylaşabilir; telif satırı korunmalıdır. Yazılım olduğu gibi, hiçbir garanti verilmeden sunulur.
