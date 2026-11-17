# Xtream STRM — Jellyfin eklentisi

Xtream hesabınızdaki film ve dizileri, seçtiğiniz kategorilerden `.strm` kısayolları olarak Jellyfin medya klasörlerine yazar. Dosyalar indirilmez; oynatınca doğrudan Xtream sunucusundan akar. Jellyfin bunları normal film/dizi gibi tanır (afiş, özet, izlenme durumu vb.).

Gereken sürüm: **Jellyfin 12.4** veya üzeri.

## Kurulum

1. Jellyfin → **Kontrol Paneli → Eklentiler → Depolar → +** ile şu adresi ekleyin:

   ```
   https://raw.githubusercontent.com/sureyyasayin/xtream/main/manifest.json
   ```

2. **Katalog** sekmesinden **Xtream STRM** eklentisini kurun ve Jellyfin'i yeniden başlatın.

## Kullanım

1. **Kontrol Paneli → Eklentiler → Xtream STRM** sayfasını açın.
2. Xtream adresini, kullanıcı adını ve şifreyi girip **Kaydet ve bağlantıyı test et**'e basın.
3. Açılan listelerden istediğiniz film, dizi ve canlı TV kategorilerini işaretleyin. Seçim yapılmayan türden içerik alınmaz.
4. Film ve dizi klasörlerinin, kütüphanelerinizin klasörleri olduğunu kontrol edin (varsayılan: `/data/movies` ve `/data/tvshows`).
5. **Kaydet ve şimdi senkronize et**'e basın. İlerleme **Zamanlanmış Görevler** altında görünür.

Senkronizasyon her gece 04:00'te kendiliğinden tekrarlanır; saat Zamanlanmış Görevler'den değiştirilebilir.

## Davranış

- Film: `Film Adı (Yıl) [tmdbid-123]/Film Adı (Yıl).strm`
- Dizi: `Dizi Adı (Yıl)/Season 01/Dizi Adı (Yıl) S01E01.strm`
- Canlı TV: seçilen kategorilerdeki kanallar Jellyfin'in **Canlı TV** bölümüne "Xtream" adlı tuner olarak eklenir; yayın akışı (EPG) yalnızca bu kanallara süzülerek bağlanır. Canlı TV kapatılırsa tuner kaldırılır.
- Kumandayla kanal değiştirme (LG webOS Jellyfin uygulaması ve tarayıcı): canlı kanal izlerken **CH+ / CH-** ve alt çubuk kapalıyken **Yukarı / Aşağı** sonraki / önceki kanala geçer; **rakam** tuşları kanal numarasına gider; **Yeşil** tuş bir önceki kanala döner. Art arda basınca ekranda kanal adı görünür, tuş bırakılınca son seçilen kanal açılır. Eklenti bunun için Jellyfin web arayüzüne küçük bir betik ekler (Jellyfin dosyalarına dokunmaz); ayarlardan kapatılabilir. Samsung (Tizen) uygulaması arayüzü kendi içinde taşıdığı için orada çalışmaz.
- Aynı isimde bir klasör zaten varsa (kendi dosyalarınız) o içerik atlanır; mevcut dosyalarınıza dokunulmaz.
- Xtream listesinden kalkan ya da seçimi kaldırılan kategorilere ait kısayollar silinir (ayarlardan kapatılabilir). Yalnızca eklentinin kendi oluşturduğu dosyalar silinir.
- Sunucuya ulaşılamazsa hiçbir dosya silinmez.
- Değişmeyen dizilerin bölüm listesi tekrar indirilmez.

## Derleme

Docker yeterlidir, .NET SDK kurmak gerekmez:

```sh
cd Jellyfin.Plugin.XtreamStrm
sh build.sh        # çıktı: out/Jellyfin.Plugin.XtreamStrm.dll
```
