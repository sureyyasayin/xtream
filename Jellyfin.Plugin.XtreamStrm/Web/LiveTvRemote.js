/*
 * Xtream STRM - Canli TV kumanda destegi (Jellyfin web istemcisine eklenir).
 *
 * Canli kanal izlerken:
 *   CH+ / CH-            sonraki / onceki kanal
 *   Yukari / Asagi       sonraki / onceki kanal (yalnizca alt cubuk gizliyken)
 *   Rakamlar             kanal numarasina gec
 *   Yesil                bir onceki kanala don
 *   Kirmizi / Mavi       onceki / sonraki Xtream kategorisinin ilk kanali
 *
 * Arka arkaya basildiginda yalnizca ekrandaki secim degisir; tus birakilinca son
 * secilen kanal acilir (tek baglantili hesapta her ara kanal acilip kapanmasin diye).
 * Kanal, Jellyfin'in kendi "uzaktan oynat" komutuyla acilir; istemciye dokunulmaz.
 */
(function () {
    'use strict';

    if (window.__xtreamLiveRemote) {
        return;
    }
    window.__xtreamLiveRemote = true;

    var cfg = window.__xtreamLiveRemoteCfg || { remote: true, arrows: true, libraryCard: true };

    var ZAP_DELAY = 650;
    var DIGIT_DELAY = 1600;
    var CHANNEL_CACHE_MS = 10 * 60 * 1000;
    var SESSION_MAX_AGE = 15000;

    var channels = null;
    var channelsAt = 0;
    var channelsLoading = null;

    // Xtream kategorileri ve numara araliklari: [{Name, First, Last}]
    var categories = [];

    var session = { id: null, itemId: null, isLive: false, at: 0, number: NaN };
    var refreshing = null;
    var previousId = null;

    var pending = -1;
    var zapTimer = null;
    var digits = '';
    var digitTimer = null;

    var overlay = null;
    var overlayTimer = null;
    var lastHref = '';
    var lastVideo = false;
    var routeTimers = [];

    function api() {
        return window.ApiClient;
    }

    function onVideoPage() {
        var re = /\/video(\?|$)/;
        return re.test(location.hash || '') || re.test(location.pathname || '');
    }

    function getJson(path, query) {
        var a = api();
        return a.ajax({ type: 'GET', url: a.getUrl(path, query), dataType: 'json' });
    }

    // "Xtream Canli TV" klasor gorunumu (Jellyfin kanali): oradan acilan kanallar da canli sayilir.
    var folderChannelId = null;
    var folderLookup = null;

    // "Xtream Filmler" / "Xtream Diziler" kutulari: ogeye tiklaninca kutuphanedeki kart acilir.
    var catalogChannelIds = {};

    function loadFolderChannelId() {
        if (folderLookup) {
            return folderLookup;
        }

        folderLookup = getJson('Channels', { UserId: api().getCurrentUserId() }).then(function (r) {
            ((r && r.Items) || []).forEach(function (c) {
                if (c.Name === 'Xtream Canlı TV') {
                    folderChannelId = c.Id;
                } else if (c.Name === 'Xtream Filmler' || c.Name === 'Xtream Diziler') {
                    catalogChannelIds[c.Id] = true;
                }
            });
            return folderChannelId;
        }, function () {
            folderLookup = null;
            return null;
        });
        return folderLookup;
    }

    function isFolderItem(item) {
        return !!item && !!folderChannelId && item.ChannelId === folderChannelId;
    }

    function isLiveItem(item) {
        return !!item && (item.Type === 'TvChannel' || item.Type === 'LiveTvChannel' || (item.Type === 'Program' && !!item.ChannelId) || isFolderItem(item));
    }

    /** Klasor gorunumundeki adlar "101 · KANAL" bicimindedir. */
    function numberFromName(name) {
        var m = /^(\d+) · /.exec(name || '');
        return m ? parseInt(m[1], 10) : NaN;
    }

    function refreshSession() {
        var a = api();
        if (!a || typeof a.deviceId !== 'function') {
            return Promise.resolve(session);
        }

        if (refreshing) {
            return refreshing;
        }

        refreshing = loadFolderChannelId().then(function () {
            return getJson('Sessions', { deviceId: a.deviceId() });
        }).then(function (list) {
            var s = null;
            (list || []).forEach(function (x) {
                if (!s || (x.NowPlayingItem && !s.NowPlayingItem)) {
                    s = x;
                }
            });

            var item = s && s.NowPlayingItem;
            session.id = s ? s.Id : null;
            session.isLive = isLiveItem(item);
            session.itemId = session.isLive ? (item.Type === 'Program' ? item.ChannelId : item.Id) : null;
            session.number = isFolderItem(item) ? numberFromName(item.Name) : NaN;
            session.at = Date.now();
            refreshing = null;
            return session;
        }, function () {
            refreshing = null;
            return session;
        });

        return refreshing;
    }

    function channelNumber(c) {
        var n = parseFloat(c.ChannelNumber);
        return isNaN(n) ? Infinity : n;
    }

    function compareChannels(x, y) {
        var a = channelNumber(x);
        var b = channelNumber(y);
        if (a !== b) {
            return a < b ? -1 : 1;
        }

        return String(x.SortName || x.Name || '').localeCompare(String(y.SortName || y.Name || ''));
    }

    function loadChannels() {
        if (channels && Date.now() - channelsAt < CHANNEL_CACHE_MS) {
            return Promise.resolve(channels);
        }

        if (channelsLoading) {
            return channelsLoading;
        }

        var a = api();
        channelsLoading = getJson('LiveTv/Channels', {
            UserId: a.getCurrentUserId(),
            EnableImages: false,
            EnableUserData: false,
            AddCurrentProgram: false
        }).then(function (r) {
            var items = ((r && r.Items) || []).slice();
            items.sort(compareChannels);
            channels = items;
            channelsAt = Date.now();
            channelsLoading = null;

            // Kategoriler istege bagli: alinamazsa yalnizca kategori ozellikleri calismaz.
            getJson('XtreamStrm/LiveCategories').then(function (c) {
                categories = Array.isArray(c) ? c : [];
            }, function () { });
            return channels;
        }, function (err) {
            channelsLoading = null;
            throw err;
        });

        return channelsLoading;
    }

    function indexOfChannel(list, id) {
        for (var i = 0; i < list.length; i++) {
            if (list[i].Id === id) {
                return i;
            }
        }

        return -1;
    }

    function findByNumber(list, text) {
        var want = parseFloat(text);
        for (var i = 0; i < list.length; i++) {
            if (String(list[i].ChannelNumber) === text || parseFloat(list[i].ChannelNumber) === want) {
                return list[i];
            }
        }

        return null;
    }

    function categoryIndex(number) {
        for (var i = 0; i < categories.length; i++) {
            if (number >= categories[i].First && number <= categories[i].Last) {
                return i;
            }
        }

        return -1;
    }

    function label(ch) {
        return (ch.ChannelNumber ? ch.ChannelNumber + '   ' : '') + (ch.Name || '');
    }

    function show(ch) {
        var ci = categoryIndex(channelNumber(ch));
        showText(label(ch), false, ci >= 0 ? categories[ci].Name : '');
    }

    function showText(text, sticky, sub) {
        if (!overlay || !document.body.contains(overlay)) {
            overlay = document.createElement('div');
            overlay.setAttribute('aria-live', 'polite');
            overlay.style.cssText = [
                'position:fixed', 'top:6%', 'right:4%', 'z-index:2147483647',
                'padding:.5em 1em', 'border-radius:.35em', 'background:rgba(0,0,0,.8)',
                'color:#fff', 'font-size:2.4em', 'font-weight:600', 'line-height:1.3',
                'max-width:60%', 'white-space:nowrap', 'overflow:hidden', 'text-overflow:ellipsis',
                'pointer-events:none', 'opacity:0', 'transition:opacity .2s'
            ].join(';');
            document.body.appendChild(overlay);
        }

        overlay.textContent = '';
        if (sub) {
            var small = document.createElement('div');
            small.style.cssText = 'font-size:.5em;font-weight:500;opacity:.75;overflow:hidden;text-overflow:ellipsis';
            small.textContent = sub;
            overlay.appendChild(small);
        }

        var main = document.createElement('div');
        main.style.cssText = 'overflow:hidden;text-overflow:ellipsis';
        main.textContent = text;
        overlay.appendChild(main);
        overlay.style.opacity = '1';
        clearTimeout(overlayTimer);
        if (!sticky) {
            overlayTimer = setTimeout(function () {
                overlay.style.opacity = '0';
            }, 2500);
        }
    }

    function playChannel(ch) {
        if (!ch || ch.Id === session.itemId) {
            return;
        }

        var start = session.id ? Promise.resolve(session) : refreshSession();
        start.then(function (s) {
            if (!s.id) {
                showText('Kanal değiştirilemedi');
                return;
            }

            var cur = channels ? currentIndex(channels) : -1;
            previousId = cur >= 0 ? channels[cur].Id : s.itemId;
            s.itemId = ch.Id;
            s.number = NaN;
            var a = api();
            a.ajax({
                type: 'POST',
                url: a.getUrl('Sessions/' + s.id + '/Playing', { playCommand: 'PlayNow', itemIds: ch.Id })
            }).then(null, function () {
                showText('Kanal açılamadı: ' + (ch.Name || ''));
            });

            // Yeni kanal oynamaya basladiktan sonra durumu dogrula.
            setTimeout(refreshSession, 5000);
        });
    }

    /** Secimi gosterir; tuslar birakilinca (ZAP_DELAY) secili kanal acilir. */
    function moveTo(list, idx) {
        pending = idx;
        show(list[idx]);
        clearTimeout(zapTimer);
        zapTimer = setTimeout(function () {
            var i = pending;
            pending = -1;
            if (i >= 0 && channels && channels[i]) {
                playChannel(channels[i]);
            }
        }, ZAP_DELAY);
    }

    function currentIndex(list) {
        if (pending >= 0) {
            return pending;
        }

        var i = indexOfChannel(list, session.itemId);
        if (i < 0 && !isNaN(session.number)) {
            // Klasor gorunumunden acilmis: Canli TV'deki ayni numarali kanal.
            for (var j = 0; j < list.length; j++) {
                if (channelNumber(list[j]) === session.number) {
                    return j;
                }
            }
        }

        return i;
    }

    function zap(dir) {
        loadChannels().then(function (list) {
            if (!list.length) {
                return;
            }

            var base = currentIndex(list);
            if (base < 0) {
                base = dir > 0 ? -1 : 0;
            }

            moveTo(list, (base + dir + list.length) % list.length);
        }, function () {
            showText('Kanal listesi alınamadı');
        });
    }

    function jumpCategory(dir) {
        loadChannels().then(function (list) {
            if (!list.length || !categories.length) {
                return;
            }

            var base = currentIndex(list);
            var ci = base >= 0 ? categoryIndex(channelNumber(list[base])) : -1;
            var n = categories.length;
            for (var step = 1; step <= n; step++) {
                var target = ci < 0 ? (dir > 0 ? step - 1 : n - step) : (ci + dir * step + n * step) % n;
                var cat = categories[target];
                for (var i = 0; i < list.length; i++) {
                    var num = channelNumber(list[i]);
                    if (num >= cat.First && num <= cat.Last) {
                        moveTo(list, i);
                        return;
                    }
                }
            }
        });
    }

    function digit(d) {
        clearTimeout(zapTimer);
        pending = -1;
        digits = (digits + d).slice(-4);
        var typed = digits;
        showText(typed + ' _', true);

        loadChannels().then(function (list) {
            if (digits !== typed) {
                return;
            }

            var ch = findByNumber(list, typed);
            showText(ch ? label(ch) : typed + ' _', true);
        });

        clearTimeout(digitTimer);
        digitTimer = setTimeout(function () {
            var want = digits;
            digits = '';
            loadChannels().then(function (list) {
                var ch = findByNumber(list, want);
                if (ch) {
                    show(ch);
                    playChannel(ch);
                } else {
                    showText(want + ': kanal yok');
                }
            });
        }, DIGIT_DELAY);
    }

    function goPrevious() {
        if (!previousId) {
            return;
        }

        loadChannels().then(function (list) {
            var i = indexOfChannel(list, previousId);
            if (i >= 0) {
                show(list[i]);
                playChannel(list[i]);
            }
        });
    }

    function keyName(e) {
        switch (e.keyCode) {
            case 33:
            case 427:
                return 'chup';
            case 34:
            case 428:
                return 'chdown';
            case 38:
                return 'up';
            case 40:
                return 'down';
            case 403:
                return 'red';
            case 404:
                return 'green';
            case 406:
                return 'blue';
            default:
                break;
        }

        if (e.key === 'ChannelUp') {
            return 'chup';
        }

        if (e.key === 'ChannelDown') {
            return 'chdown';
        }

        if (e.keyCode >= 48 && e.keyCode <= 57) {
            return 'd' + (e.keyCode - 48);
        }

        if (e.keyCode >= 96 && e.keyCode <= 105) {
            return 'd' + (e.keyCode - 96);
        }

        return null;
    }

    function osdVisible() {
        var els = document.querySelectorAll('.videoOsdBottom');
        for (var i = 0; i < els.length; i++) {
            var el = els[i];
            if (el.offsetParent !== null && !el.classList.contains('videoOsdBottom-hidden') && !el.classList.contains('hide')) {
                return true;
            }
        }

        return false;
    }

    function dialogOpen() {
        // Jellyfin her acik pencere icin bir .dialogContainer olusturur, kapaninca kaldirir.
        return !!document.querySelector('.dialogContainer');
    }

    function handle(k) {
        if (k === 'chup' || k === 'up') {
            zap(1);
        } else if (k === 'chdown' || k === 'down') {
            zap(-1);
        } else if (k === 'green') {
            goPrevious();
        } else if (k === 'red') {
            jumpCategory(-1);
        } else if (k === 'blue') {
            jumpCategory(1);
        } else if (k.charAt(0) === 'd') {
            digit(k.charAt(1));
        }
    }

    function consume(e) {
        e.preventDefault();
        e.stopPropagation();
        if (e.stopImmediatePropagation) {
            e.stopImmediatePropagation();
        }
    }

    function onKey(e) {
        if (cfg.remote === false || e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) {
            return;
        }

        var k = keyName(e);
        if (!k || !api() || !onVideoPage() || dialogOpen()) {
            return;
        }

        var isArrow = k === 'up' || k === 'down';
        if (isArrow && (!cfg.arrows || osdVisible())) {
            return;
        }

        // Donulecek kanal yokken Yesil tus Jellyfin'e kalir.
        if (k === 'green' && !previousId) {
            return;
        }

        // Kategori bilgisi yoksa Kirmizi / Mavi Jellyfin'e kalir.
        if ((k === 'red' || k === 'blue') && !categories.length) {
            return;
        }

        if (session.isLive && Date.now() - session.at < 60000) {
            consume(e);
            handle(k);
            return;
        }

        // Durum bilinmiyor: kanal tuslari baska bir ise yaramadigi icin sorup devam et.
        if (k === 'chup' || k === 'chdown') {
            consume(e);
            refreshSession().then(function (s) {
                if (s.isLive) {
                    handle(k);
                }
            });
        }
    }

    /**
     * Xtream Filmler / Diziler kutusundan bir film, dizi, sezon ya da bolum sayfasi acildiysa
     * kutuphanedeki karsiliginin kartina gecer (afis, ozet, izlenme durumu kutuphanedeki gibi).
     */
    var redirecting = {};

    function openLibraryCard() {
        var m = /[#/](details|list)\?(.*)$/.exec(location.hash || '');
        if (!m || !api() || cfg.libraryCard === false) {
            return;
        }

        var params = {};
        m[2].split('&').forEach(function (kv) {
            var i = kv.indexOf('=');
            if (i > 0) {
                params[kv.slice(0, i)] = decodeURIComponent(kv.slice(i + 1));
            }
        });

        var id = m[1] === 'details' ? params.id : params.parentId;
        if (!id || redirecting[id]) {
            return;
        }

        redirecting[id] = true;
        var a = api();
        loadFolderChannelId().then(function () {
            return getJson('Users/' + a.getCurrentUserId() + '/Items/' + id);
        }).then(function (item) {
            if (!item || !catalogChannelIds[item.ChannelId]) {
                return null;
            }

            return getJson('XtreamStrm/LibraryItem/' + id);
        }).then(function (r) {
            var target = r && r.id;
            // Kullanici bu arada baska sayfaya gectiyse yonlendirme yapilmaz.
            if (target && location.hash.indexOf(id) !== -1) {
                var serverId = params.serverId || (a.serverId && a.serverId()) || '';
                location.replace('#/details?id=' + target + (serverId ? '&serverId=' + serverId : ''));
            }
        }).then(function () {
            delete redirecting[id];
        }, function () {
            delete redirecting[id];
        });
    }

    /*
     * Kutudaki kart hic gorunmesin diye tiklama sayfa acilmadan yakalanir: Xtream Filmler /
     * Diziler listesindeyken bir karta tiklaninca kutuphanedeki karsiligi sorulur, varsa
     * dogrudan onun karti acilir; yoksa (kategori klasoru gibi) tiklama normal devam eder.
     */
    var interceptParent = null;
    var libraryCache = {};

    function hashParam(name) {
        var m = new RegExp('[?&]' + name + '=([^&]+)').exec(location.hash || '');
        return m ? decodeURIComponent(m[1]) : null;
    }

    function updateIntercept() {
        interceptParent = null;
        var parentId = /[#/]list\?/.test(location.hash || '') ? hashParam('parentId') : null;
        if (!parentId || !api() || cfg.libraryCard === false) {
            return;
        }

        var a = api();
        loadFolderChannelId().then(function () {
            if (catalogChannelIds[parentId]) {
                return { ChannelId: parentId };
            }

            return getJson('Users/' + a.getCurrentUserId() + '/Items/' + parentId);
        }).then(function (item) {
            if (item && catalogChannelIds[item.ChannelId] && hashParam('parentId') === parentId) {
                interceptParent = parentId;
            }
        }, function () { });
    }

    function lookupLibrary(id) {
        if (Object.prototype.hasOwnProperty.call(libraryCache, id)) {
            return Promise.resolve(libraryCache[id]);
        }

        return getJson('XtreamStrm/LibraryItem/' + id).then(function (r) {
            libraryCache[id] = (r && r.id) || null;
            return libraryCache[id];
        }, function () {
            return null;
        });
    }

    function onCardClick(e) {
        if (!interceptParent || hashParam('parentId') !== interceptParent || !e.target || !e.target.closest) {
            return;
        }

        var card = e.target.closest('[data-id]');
        if (!card || card.getAttribute('data-xtream-skip')) {
            return;
        }

        // Kart uzerindeki oynat / menu dugmeleri kendi isini yapsin.
        var action = e.target.closest('[data-action]');
        if (action && action !== card && action.getAttribute('data-action') !== 'link') {
            return;
        }

        var id = card.getAttribute('data-id');
        consume(e);
        lookupLibrary(id).then(function (target) {
            if (target) {
                var a = api();
                var serverId = card.getAttribute('data-serverid') || (a.serverId && a.serverId()) || '';
                location.hash = '#/details?id=' + target + (serverId ? '&serverId=' + serverId : '');
            } else {
                card.setAttribute('data-xtream-skip', '1');
                card.click();
                card.removeAttribute('data-xtream-skip');
            }
        });
    }

    window.addEventListener('click', onCardClick, true);

    function onRoute(wasVideo) {
        openLibraryCard();
        updateIntercept();

        routeTimers.forEach(clearTimeout);
        routeTimers = [];

        // Kanal degisirken oynatici sayfasi yeniden acilabilir; o durumda bilgi korunur.
        if (!wasVideo || !onVideoPage()) {
            session.isLive = false;
            session.itemId = null;
            pending = -1;
            digits = '';
        }

        if (onVideoPage()) {
            // Oynatma bilgisi sunucuya birkac saniye icinde ulasir.
            [1500, 4000, 9000].forEach(function (ms) {
                routeTimers.push(setTimeout(refreshSession, ms));
            });
            loadChannels().then(null, function () { });
        }
    }

    function tick() {
        if (location.href !== lastHref) {
            var wasVideo = lastVideo;
            lastHref = location.href;
            lastVideo = onVideoPage();
            onRoute(wasVideo);
        } else if (onVideoPage() && api() && Date.now() - session.at > SESSION_MAX_AGE) {
            refreshSession();
        }
    }

    window.addEventListener('keydown', onKey, true);
    setInterval(tick, 1000);
})();
