/*
 * 卡片进度角标（PLAN M5.5）。
 *
 * Jellyfin 网页的卡片只有进度条、没有数字时间，这个脚本在卡片图片的左上角贴一个小标签：
 *   有进度未看完 → 16:08 / 30:22
 *   没看过       → 0:00 / 30:22
 *   已看完       → 已看完
 *   拿不到总时长 → 不显示
 *
 * 为什么放左上角：右下角是进度条/操作按钮、右上角是已看对勾，只有左上角是空的。
 * 为什么不用卡片上的 data-positionticks：它只在「继续观看」卡片上有，且没有总时长与已看状态。
 */
(function () {
    'use strict';

    if (window.__ppbProgressBadge) {
        return;
    }

    window.__ppbProgressBadge = true;

    var BADGE_ATTR = 'data-ppb-badge';
    var CONFIG = (window.PotPlayerBridge && window.PotPlayerBridge.config) || {};
    var BATCH_SIZE = 60;
    var REFRESH_DEBOUNCE_MS = 250;

    // itemId -> { positionTicks, runtimeTicks, played }；整个会话共用一份缓存，
    // 路由来回切换时不重复查询。
    var cache = new Map();
    var pending = new Set();
    var timer = null;
    var inFlight = false;

    function log(message, detail) {
        if (typeof detail === 'undefined') {
            console.log('[PotPlayerBridge] ' + message);
        } else {
            console.log('[PotPlayerBridge] ' + message, detail);
        }
    }

    /** 把 ticks 转成 16:08 / 1:02:33。 */
    function formatTicks(ticks) {
        if (!ticks || ticks <= 0) {
            return '0:00';
        }

        var totalSeconds = Math.floor(ticks / 10000000);
        var hours = Math.floor(totalSeconds / 3600);
        var minutes = Math.floor((totalSeconds % 3600) / 60);
        var seconds = totalSeconds % 60;

        var mm = hours > 0 && minutes < 10 ? '0' + minutes : String(minutes);
        var ss = seconds < 10 ? '0' + seconds : String(seconds);

        return hours > 0 ? hours + ':' + mm + ':' + ss : mm + ':' + ss;
    }

    /**
     * 只有「指向具体某集/某部电影」的卡片才显示角标。
     * 剧（Series）、季（Season）、合集（BoxSet）不显示：它们每集的时长都不一样，
     * 在卡片上写一个总时长只会误导（用户在首页看到剧卡片时想看的是"这剧多长"，而这个数字并不存在）。
     */
    var SHOWABLE_TYPES = { Episode: 1, Movie: 1, Video: 1 };

    function buildText(info) {
        if (!info || !SHOWABLE_TYPES[info.itemType]) {
            return null;
        }

        // 没有时长元数据（STRM 常见）就不显示：少一个数字比显示「0:00 / 0:00」好。
        if (!info.runtimeTicks || info.runtimeTicks <= 0) {
            return null;
        }

        if (info.played) {
            return '已看完';
        }

        var position = info.positionTicks > info.runtimeTicks ? info.runtimeTicks : info.positionTicks;
        return formatTicks(position) + ' / ' + formatTicks(info.runtimeTicks);
    }

    /**
     * 找到「图片容器」作为角标的定位父级。
     * 不同模板类名不同，按优先级依次尝试：卡片用 .cardScalable，季页的剧集行用 .listItemImage。
     * 找不到就不显示 —— 宁可不显示，也不要把角标贴到错误的位置。
     */
    function findImageHost(card) {
        return card.querySelector('.cardScalable')
            || card.querySelector('.listItemImage')
            || card.querySelector('.cardImageContainer')
            || card.querySelector('.cardBox')
            || card.querySelector('.card');
    }

    function applyBadge(card) {
        var itemId = card.getAttribute('data-id');
        if (!itemId) {
            return;
        }

        var host = findImageHost(card);
        if (!host) {
            return;
        }

        var info = cache.get(normalizeId(itemId));
        var text = buildText(info);
        var existing = host.querySelector('[' + BADGE_ATTR + ']');

        if (!text) {
            // 没有数据时把旧角标撤掉：进度可能刚被清空（例如标记为未看）。
            if (existing) {
                existing.remove();
            }
            return;
        }

        if (existing) {
            if (existing.textContent !== text) {
                existing.textContent = text;
            }
            return;
        }

        var badge = document.createElement('div');
        badge.setAttribute(BADGE_ATTR, '1');
        badge.textContent = text;
        badge.style.cssText = [
            'position:absolute', 'top:6px', 'left:6px', 'z-index:2',
            'padding:2px 7px', 'border-radius:4px',
            'background:rgba(0,0,0,.72)', 'color:#fff',
            'font-size:11px', 'line-height:1.5', 'font-weight:500',
            'letter-spacing:.2px', 'pointer-events:none',
            'text-shadow:0 1px 2px rgba(0,0,0,.6)',
            'font-variant-numeric:tabular-nums', 'white-space:nowrap'
        ].join(';');

        // 角标要相对图片定位：Jellyfin 的卡片容器不一定都是 position:relative。
        if (getComputedStyle(host).position === 'static') {
            host.style.position = 'relative';
        }

        host.appendChild(badge);
    }

    function normalizeId(value) {
        return String(value || '').replace(/-/g, '').toLowerCase();
    }

    /** 从路由里取当前详情页的 itemId（详情页海报没有 data-id 属性）。 */
    function detailPageId() {
        var match = /[?&]id=([0-9a-fA-F-]{32,36})/.exec(window.location.hash || '');
        return match ? normalizeId(match[1]) : null;
    }

    /** 收集当前页面上所有需要角标的卡片、季页剧集行与详情页海报。 */
    function collectTargets() {
        var targets = [];
        var seen = new Set();

        // .card：首页/媒体库/「接下来」；.listItem：季页的剧集行（不是 .card）。
        document.querySelectorAll('.card[data-id], .listItem[data-id]').forEach(function (element) {
            var id = normalizeId(element.getAttribute('data-id'));
            if (!id || !element.getClientRects().length) {
                return;
            }

            // 首页/媒体库里的剧卡片、季卡片直接跳过：既不显示角标，也不用为它发请求。
            var domType = element.getAttribute('data-type');
            if (domType && !SHOWABLE_TYPES[domType]) {
                return;
            }

            targets.push(element);
            seen.add(id);
        });

        // 详情页的海报不是 .card、也没有 data-id，但它对应路由里的那个 id。
        // 注意不能按 seen 去重：详情页里同一个 item 往往还有「接下来」卡片，
        // 去重会让海报永远轮不到角标。
        var detailId = detailPageId();
        if (detailId) {
            var posters = document.querySelectorAll('.detailImageContainer');
            for (var i = 0; i < posters.length; i++) {
                if (!posters[i].getClientRects().length) {
                    continue;
                }

                // 海报外层没有 data-id，把路由里的 id 记上去供 applyBadge 使用。
                if (!posters[i].hasAttribute('data-id')) {
                    posters[i].setAttribute('data-id', detailId);
                }

                targets.push(posters[i]);
                break;
            }
        }

        return targets;
    }

    function scheduleRefresh() {
        if (timer) {
            window.clearTimeout(timer);
        }

        timer = window.setTimeout(refresh, REFRESH_DEBOUNCE_MS);
    }

    function refresh() {
        timer = null;

        var targets = collectTargets();

        // 先用缓存把已知的角标贴上去（页面切换后新卡片能立刻显示）。
        targets.forEach(applyBadge);

        // 再把没查过的 itemId 收集起来批量查。
        targets.forEach(function (element) {
            var id = normalizeId(element.getAttribute('data-id'));
            if (id && !cache.has(id)) {
                pending.add(id);
            }
        });

        flushPending();
    }

    function flushPending() {
        if (inFlight || pending.size === 0) {
            return;
        }

        var api = window.PotPlayerBridge && window.PotPlayerBridge.api;
        if (!api) {
            return;
        }

        var batch = Array.from(pending).slice(0, BATCH_SIZE);
        batch.forEach(function (id) {
            pending.delete(id);
        });

        inFlight = true;

        api.get('ItemProgress', { itemIds: batch.join(',') })
            .then(function (payload) {
                var items = (payload && payload.items) || [];
                items.forEach(function (item) {
                    cache.set(normalizeId(item.itemId), item);
                });

                // 查不到的不缓存「失败」结果：插件刚部署完、Jellyfin 媒体库还没就绪时，
                // GetItemById 会返回空，如果把 null 记进缓存，这张卡片就永远不显示角标了。
                // 不缓存 null 的代价只是下次刷新再查一次，有 debounce 兜着不会打爆服务器。
                inFlight = false;
                collectTargets().forEach(applyBadge);
                flushPending();
            })
            .catch(function (error) {
                inFlight = false;
                log('查询卡片进度失败：' + (error && error.message ? error.message : error));
            });
    }

    /** 本地进度或已看状态变了，把对应 item 的缓存作废后重算。 */
    function invalidate(itemIds) {
        (itemIds || []).forEach(function (raw) {
            cache.delete(normalizeId(raw));
        });

        scheduleRefresh();
    }

    // 路由切换：Jellyfin 会复用同一批 DOM 节点，只是内容变了，所以两种信号都要听。
    window.addEventListener('hashchange', scheduleRefresh);

    // 懒加载：季页的剧集行、首页的长列表都是滚到才渲染/变可见，
    // 只靠 hashchange 会漏掉后来才出现的卡片，所以滚动时也要重算（2 秒节流）。
    var lastScrollAt = 0;
    window.addEventListener('scroll', function () {
        var now = Date.now();
        if (now - lastScrollAt < 2000) {
            return;
        }

        lastScrollAt = now;
        scheduleRefresh();
    }, { passive: true });

    // 懒加载图片、分页追加、Vue 重渲染都会改 DOM；只在新增节点时才重算，避免自己改自己触发循环。
    var observer = new MutationObserver(function (records) {
        for (var i = 0; i < records.length; i++) {
            var record = records[i];
            if (record.type !== 'childList' || record.addedNodes.length === 0) {
                continue;
            }

            for (var j = 0; j < record.addedNodes.length; j++) {
                var node = record.addedNodes[j];
                if (node.nodeType !== 1) {
                    continue;
                }

                // 自己插入的角标会触发一次 childList，跳过它。
                if (node.hasAttribute && node.hasAttribute(BADGE_ATTR)) {
                    continue;
                }

                if ((node.matches && (node.matches('.card[data-id]') || node.matches('.listItem[data-id]') || node.matches('.cardBox')))
                    || (node.querySelector && node.querySelector('.card[data-id], .listItem[data-id]'))) {
                    scheduleRefresh();
                    return;
                }
            }
        }
    });

    observer.observe(document.body, { childList: true, subtree: true });

    // 收到服务器的 UserDataChanged 时刷新角标，用户暂停/看完后能立刻看到时间变化。
    function hookWebSocket(attempt) {
        var api = window.ApiClient;
        if (!api || typeof api.getCurrentUserId !== 'function' || !api.getCurrentUserId()) {
            return false;
        }

        // Jellyfin Web 自己管理 ApiClient 的事件总线；订阅 userdata 即可，
        // 不自己开 WebSocket（否则会和网页抢连接，服务端日志也会多一份噪音）。
        if (window.Events && typeof window.Events.on === 'function') {
            window.Events.on(window.ApiClient, 'userdatachanged', function (e, payload) {
                var ids = [];

                if (payload && payload.UserDataList) {
                    payload.UserDataList.forEach(function (d) {
                        if (d && d.ItemId) {
                            ids.push(d.ItemId);
                        }
                    });
                }

                if (ids.length === 0) {
                    // 有些事件只带 ItemId 不带列表；全量失效一次，代价是一次批量请求。
                    cache.clear();
                } else {
                    ids.forEach(function (raw) {
                        cache.delete(normalizeId(raw));
                    });
                }

                scheduleRefresh();
            });

            // Jellyfin 路由切换后触发 viewshow：此时新页面的卡片已经渲染，是最可靠的刷新时机。
            window.Events.on(window.ApiClient, 'viewshow', scheduleRefresh);

            log('卡片进度角标已就绪（监听 UserDataChanged 与 viewshow）。');
            return true;
        }

        // 脚本可能比 Jellyfin 的事件总线更早执行（实测常见），必须持续重试而不是只试一次，
        // 否则「暂停后角标不更新」会在部分用户机器上随机出现。
        attempt = attempt || 0;
        if (attempt < 30) {
            window.setTimeout(function () {
                hookWebSocket(attempt + 1);
            }, 2000);
        }

        return false;
    }

    if (!hookWebSocket(0)) {
        log('卡片进度角标已就绪（事件总线尚未就绪，稍后重试订阅）。');
    }

    /** 强制丢弃缓存并重算（调试/手动刷新用）。 */
    function forceRefresh() {
        cache.clear();
        pending.clear();
        scheduleRefresh();
    }

    // 切回标签页时重算一次：长时间在后台时 WebSocket 可能已经断开重连，中间的事件会丢。
    document.addEventListener('visibilitychange', function () {
        if (!document.hidden) {
            forceRefresh();
        }
    });

    // 调试出口：角标自己维护缓存，出问题时（某类卡片一直没角标）
    // 可以在控制台执行 __ppbBadgeDebug() 看到缓存与待查队列，不用反复改代码重部署。
    window.__ppbBadgeDebug = function (itemId) {
        var targets = collectTargets();
        var detail = null;

        if (itemId) {
            var key = normalizeId(itemId);
            detail = { id: key.slice(0, 8), cached: cache.get(key) || null, inCache: cache.has(key) };
        }

        return {
            cacheSize: cache.size,
            pendingSize: pending.size,
            inFlight: inFlight,
            targets: targets.length,
            targetKinds: targets.reduce(function (acc, el) {
                var kind = el.classList.contains('listItem')
                    ? 'listItem'
                    : (el.classList.contains('card') ? 'card' : 'other');
                acc[kind] = (acc[kind] || 0) + 1;
                return acc;
            }, {}),
            withBadge: document.querySelectorAll('[' + BADGE_ATTR + ']').length,
            sampleCache: Array.from(cache.entries()).slice(0, 6).map(function (pair) {
                return { id: pair[0].slice(0, 8), rt: pair[1] ? pair[1].runtimeTicks : null };
            }),
            detail: detail
        };
    };

    window.__ppbBadgeRefresh = forceRefresh;

    scheduleRefresh();
})();
