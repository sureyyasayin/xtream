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

## Ayarlar hakkında

- **İstekler arası bekleme:** Çoğu Xtream sağlayıcısı kısa sürede çok istek gelince bir süre yanıt vermez. Varsayılan 2500 ms çoğu sağlayıcı için güvenlidir; sağlayıcınız izin veriyorsa düşürebilirsiniz.
- **Aynı anda izlenebilecek kanal sayısı:** Hesabınızın bağlantı sınırını girin (çoğu hesapta 1). 0 sınırsız demektir.
- **Aynı kanalın farklı kaliteleri:** Birçok sağlayıcı aynı kanalı HD, UHD, 4K gibi ayrı kategorilerde verir. Canlı TV'de her türün tek bir kalite kategorisini seçerseniz kanal listesi ve kumandayla kanal geçişi daha düzenli olur.

## Davranış

- Film: `Film Adı (Yıl) [tmdbid-123]/Film Adı (Yıl).strm`
- Dizi: `Dizi Adı (Yıl)/Season 01/Dizi Adı (Yıl) S01E01.strm`
- Canlı TV: seçilen kategorilerdeki kanallar Jellyfin'in **Canlı TV** bölümüne "Xtream" adlı tuner olarak eklenir; kanallara sıra numarası verilir; yayın akışı (EPG) yalnızca bu kanallara süzülerek bağlanır. Canlı TV kapatılırsa tuner kaldırılır.
- Xtream hesabı ya da adresi değişince Jellyfin'de kalan eski (artık açılmayan) kanal kayıtları kendiliğinden temizlenir.
- Kumandayla kanal değiştirme (LG webOS Jellyfin uygulaması ve tarayıcı): canlı kanal izlerken **CH+ / CH-** ve alt çubuk kapalıyken **Yukarı / Aşağı** sonraki / önceki kanala geçer; **rakam** tuşları kanal numarasına gider; **Yeşil** tuş bir önceki kanala döner. Art arda basınca ekranda kanal adı görünür, tuş bırakılınca son seçilen kanal açılır. Tek bağlantılı hesapta da çalışır: aynı cihaz yeni kanal isteyince önceki yayını kapatılır (başka cihazların yayınlarına dokunulmaz). Eklenti bunun için Jellyfin web arayüzüne küçük bir betik ekler (Jellyfin dosyalarına dokunmaz); ayarlardan kapatılabilir. Samsung (Tizen), Android TV ve diğer yerel uygulamalar arayüzü kendi içinde taşıdığı için bu özellik orada çalışmaz; canlı TV'nin kendisi her uygulamada çalışır.
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
