/*
 * Xtream STRM - Canli TV kumanda destegi (Jellyfin web istemcisine eklenir).
 *
 * Canli kanal izlerken:
 *   CH+ / CH-            sonraki / onceki kanal
 *   Yukari / Asagi       sonraki / onceki kanal (yalnizca alt cubuk gizliyken)
 *   Rakamlar             kanal numarasina gec
 *   Yesil                bir onceki kanala don
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

    var cfg = window.__xtreamLiveRemoteCfg || { arrows: true };

    var ZAP_DELAY = 650;
    var DIGIT_DELAY = 1600;
    var CHANNEL_CACHE_MS = 10 * 60 * 1000;
    var SESSION_MAX_AGE = 15000;

    var channels = null;
    var channelsAt = 0;
    var channelsLoading = null;

    var session = { id: null, itemId: null, isLive: false, at: 0 };
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

    function isLiveItem(item) {
        return !!item && (item.Type === 'TvChannel' || item.Type === 'LiveTvChannel' || (item.Type === 'Program' && !!item.ChannelId));
    }

    function refreshSession() {
        var a = api();
        if (!a || typeof a.deviceId !== 'function') {
            return Promise.resolve(session);
        }

        if (refreshing) {
            return refreshing;
        }

        refreshing = getJson('Sessions', { deviceId: a.deviceId() }).then(function (list) {
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

    function label(ch) {
        return (ch.ChannelNumber ? ch.ChannelNumber + '   ' : '') + (ch.Name || '');
    }

    function showText(text, sticky) {
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

        overlay.textContent = text;
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

            previousId = s.itemId;
            s.itemId = ch.Id;
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

    function zap(dir) {
        loadChannels().then(function (list) {
            if (!list.length) {
                return;
            }

            var base = pending >= 0 ? pending : indexOfChannel(list, session.itemId);
            if (base < 0) {
                base = dir > 0 ? -1 : 0;
            }

            pending = (base + dir + list.length) % list.length;
            showText(label(list[pending]));
            clearTimeout(zapTimer);
            zapTimer = setTimeout(function () {
                var idx = pending;
                pending = -1;
                if (idx >= 0 && channels && channels[idx]) {
                    playChannel(channels[idx]);
                }
            }, ZAP_DELAY);
        }, function () {
            showText('Kanal listesi alınamadı');
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
                    showText(label(ch));
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
                showText(label(list[i]));
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
            case 404:
                return 'green';
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
        if (e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) {
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

    function onRoute(wasVideo) {
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
