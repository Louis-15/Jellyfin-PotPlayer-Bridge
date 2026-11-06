/*
 * 接管 jellyfin-web 的播放按钮（PLAN 4 / 5.3）。
 *
 * 只回答三件事：用户点的是哪个 DOM、itemId 是什么、意图是什么；
 * 「播哪一集」全部交给服务器插件解析，前端不写剧集/季/集逻辑。
 *
 * 任何失败都必须给出可见的中文提示，绝不回退到 Jellyfin 网页播放器，
 * 否则用户会以为「点了没反应」或「设置没生效」。
 */
(function () {
    'use strict';

    if (window.__ppbPlayClient) {
        return;
    }

    window.__ppbPlayClient = true;

    var CONFIG = (window.PotPlayerBridge && window.PotPlayerBridge.config) || {};
    var AGENT_BASE = (CONFIG.agentBaseUrl || 'http://127.0.0.1:18927').replace(/\/+$/, '');
    var HEALTH_TIMEOUT = CONFIG.healthTimeoutMs || 800;
    var START_TIMEOUT = CONFIG.startTimeoutMs || 15000;

    var overlay = null;
    var busy = false;

    function log(message, detail) {
        if (typeof detail === 'undefined') {
            console.log('[PotPlayerBridge] ' + message);
        } else {
            console.log('[PotPlayerBridge] ' + message, detail);
        }
    }

    // ---------- 覆盖层 ----------

    function ensureOverlay() {
        if (overlay) {
            return overlay;
        }

        overlay = document.createElement('div');
        overlay.setAttribute('data-ppb', 'play-overlay');
        overlay.style.cssText = [
            'position:fixed', 'inset:0', 'z-index:2147483000',
            'display:none', 'align-items:center', 'justify-content:center',
            'background:rgba(0,0,0,.55)', 'font-family:"Microsoft YaHei UI","Segoe UI",sans-serif'
        ].join(';');

        var card = document.createElement('div');
        card.style.cssText = [
            'max-width:460px', 'min-width:320px', 'padding:22px 24px',
            'border-radius:10px', 'background:#1b1b1b', 'color:rgba(255,255,255,.9)',
            'box-shadow:0 12px 40px rgba(0,0,0,.5)', 'line-height:1.7', 'font-size:14px'
        ].join(';');

        var title = document.createElement('div');
        title.setAttribute('data-ppb-title', '1');
        title.style.cssText = 'font-size:17px;font-weight:600;margin-bottom:10px';
        title.textContent = 'PotPlayer';

        var message = document.createElement('div');
        message.setAttribute('data-ppb-message', '1');
        message.style.cssText = 'color:rgba(255,255,255,.78);word-break:break-word';

        var actions = document.createElement('div');
        actions.setAttribute('data-ppb-actions', '1');
        actions.style.cssText = 'display:none;margin-top:18px;gap:10px;justify-content:flex-end';

        var settings = document.createElement('a');
        settings.href = 'potplayerbridge://settings';
        settings.textContent = '打开设置';
        settings.style.cssText = 'color:#4fc3f7;text-decoration:none;margin-right:16px;align-self:center';

        var close = document.createElement('button');
        close.type = 'button';
        close.textContent = '关闭';
        close.style.cssText = [
            'padding:8px 16px', 'border-radius:6px', 'border:1px solid rgba(255,255,255,.2)',
            'background:rgba(255,255,255,.1)', 'color:#fff', 'cursor:pointer'
        ].join(';');
        close.addEventListener('click', hideOverlay);

        actions.appendChild(settings);
        actions.appendChild(close);

        card.appendChild(title);
        card.appendChild(message);
        card.appendChild(actions);
        overlay.appendChild(card);
        document.body.appendChild(overlay);
        return overlay;
    }

    function showOverlay(titleText, messageText, showActions) {
        var element = ensureOverlay();
        element.querySelector('[data-ppb-title]').textContent = titleText;
        element.querySelector('[data-ppb-message]').textContent = messageText;
        element.querySelector('[data-ppb-actions]').style.display = showActions ? 'flex' : 'none';
        element.style.display = 'flex';
    }

    function hideOverlay() {
        if (overlay) {
            overlay.style.display = 'none';
        }
    }

    // ---------- 点击识别 ----------

    function normalizeId(value) {
        if (!value) {
            return null;
        }

        var id = String(value).replace(/-/g, '').toLowerCase();
        return /^[0-9a-f]{32}$/.test(id) ? id : null;
    }

    function detailItemId() {
        var match = /[?&]id=([0-9a-fA-F-]{32,36})/.exec(window.location.hash || '');
        return match ? normalizeId(match[1]) : null;
    }

    function resolveIntent(target, card) {
        // 重播按钮：明确从 0 开始（片头是否跳过仍由季配置决定）。
        if (target.classList.contains('btnReplay')) {
            return 'restart';
        }

        if (!card) {
            // 详情页的主播放按钮：具体播哪一集/哪部电影由服务器按 item 规则解析。
            return 'item';
        }

        // 「接下来」区块：标题容器里有 Jellyfin 自己生成的 type=nextup 链接，语言无关。
        var section = card.closest('.verticalSection') || card.closest('.itemsContainer');
        if (section && section.querySelector('a[href*="type=nextup"]')) {
            return 'restart';
        }

        // 「继续观看」卡片带 data-positionticks；有进度即续播。
        var position = card.getAttribute('data-positionticks') || '';
        if (/[1-9]/.test(position)) {
            return 'resume';
        }

        var action = (target.getAttribute('data-action') || '').toLowerCase();
        if (action === 'resume') {
            return 'resume';
        }

        return 'item';
    }

    function onClick(event) {
        // 只处理左键单击；带修饰键时保留浏览器/用户原有行为。
        if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) {
            return;
        }

        // 网页播放器页面不接管。
        if ((window.location.hash || '').indexOf('#/video') === 0) {
            return;
        }

        var target = event.target.closest('[data-action], .btnPlay, .btnReplay');
        if (!target) {
            return;
        }

        // 明确排除：收藏、标记已看、更多、随机播放等。
        if (target.closest('.btnPlaystate, .btnUserRating, .btnMoreCommands, .btnShuffle, .btnDownload')) {
            return;
        }

        var action = (target.getAttribute('data-action') || '').toLowerCase();
        if (action && action !== 'play' && action !== 'resume') {
            return;
        }

        // 卡片和季页的剧集行都要认：季页用的是 .listItem[data-id]，不是 .card。
        var card = target.closest('.card[data-id], .listItem[data-id]');
        var itemId = card ? normalizeId(card.getAttribute('data-id')) : detailItemId();
        if (!itemId) {
            return;
        }

        var intent = resolveIntent(target, card);

        // 一旦判定为播放按钮，就完全接管：即使后面失败也不允许网页播放器接手。
        event.preventDefault();
        event.stopPropagation();

        startPlay(itemId, intent);
    }

    // ---------- 与本机代理通信 ----------

    function baseHeaders(extra) {
        var headers = { 'X-PPB-Client': '1', Accept: 'application/json' };
        if (extra) {
            for (var key in extra) {
                if (Object.prototype.hasOwnProperty.call(extra, key)) {
                    headers[key] = extra[key];
                }
            }
        }

        return headers;
    }

    function healthOnce() {
        var controller = new AbortController();
        var timer = window.setTimeout(function () {
            controller.abort();
        }, HEALTH_TIMEOUT);

        return fetch(AGENT_BASE + '/health', {
            headers: baseHeaders(),
            cache: 'no-store',
            signal: controller.signal
        }).then(function (response) {
            window.clearTimeout(timer);
            return response.ok;
        }, function () {
            window.clearTimeout(timer);
            return false;
        });
    }

    function ensureAgent() {
        return healthOnce().then(function (ok) {
            if (ok) {
                return null;
            }

            showOverlay('正在启动本地桥接程序…', '浏览器可能会询问是否打开 PotPlayer Bridge，请选择「打开」。', false);

            // 自定义协议拉起本地程序（若未安装，浏览器不会有任何反应，下面按超时处理）。
            try {
                window.location.href = 'potplayerbridge://start';
            } catch (error) {
                // 某些浏览器会直接抛错，忽略即可，由轮询给出最终结论。
            }

            var deadline = Date.now() + START_TIMEOUT;
            return new Promise(function (resolve, reject) {
                (function poll() {
                    healthOnce().then(function (alive) {
                        if (alive) {
                            resolve();
                            return;
                        }

                        if (Date.now() > deadline) {
                            reject(new Error('无法启动本地 PotPlayer 桥接程序。可能尚未安装，或安装已损坏。'));
                            return;
                        }

                        window.setTimeout(poll, 500);
                    });
                })();
            });
        });
    }

    function postPlay(itemId, intent) {
        var api = window.PotPlayerBridge && window.PotPlayerBridge.api;
        var token = api ? api.token() : null;

        if (!token) {
            return Promise.reject(new Error('登录状态已失效，请刷新页面后重新登录。'));
        }

        var userId = null;
        try {
            userId = window.ApiClient && window.ApiClient.getCurrentUserId
                ? window.ApiClient.getCurrentUserId()
                : null;
        } catch (error) {
            userId = null;
        }

        return fetch(AGENT_BASE + '/api/play', {
            method: 'POST',
            headers: baseHeaders({ 'Content-Type': 'application/json' }),
            cache: 'no-store',
            body: JSON.stringify({
                serverUrl: window.location.origin,
                accessToken: token,
                userId: userId,
                itemId: itemId,
                intent: intent
            })
        }).then(function (response) {
            return response.text().then(function (text) {
                var payload = null;
                try {
                    payload = text ? JSON.parse(text) : null;
                } catch (error) {
                    payload = null;
                }

                if (!response.ok) {
                    var message = payload && payload.message
                        ? payload.message
                        : '本地代理返回 HTTP ' + response.status;
                    if (response.status === 401) {
                        message = '登录状态已失效，请刷新页面后重新登录。';
                    }

                    throw new Error(message);
                }

                return payload;
            });
        });
    }

    function startPlay(itemId, intent) {
        if (busy) {
            showOverlay('PotPlayer 正在启动', '上一次播放还在处理中，请稍候。', false);
            return;
        }

        busy = true;
        showOverlay('正在启动 PotPlayer…', '正在向本机桥接程序发送播放请求。', false);
        log('拦截播放：item=' + itemId + '，intent=' + intent);

        ensureAgent()
            .then(function () {
                showOverlay('正在准备播放…', '正在解析剧集并生成播放列表。', false);
                return postPlay(itemId, intent);
            })
            .then(function (result) {
                busy = false;
                hideOverlay();
                log('播放已开始：' + (result && result.playlistName ? result.playlistName : itemId) +
                    '（第 ' + (result && result.startIndex) + ' 项）');
            })
            .catch(function (error) {
                busy = false;
                log('播放失败：' + error.message);
                showOverlay('无法用 PotPlayer 播放', error.message, true);
            });
    }

    document.addEventListener('click', onClick, true);
    log('播放按钮接管已启用');
})();
