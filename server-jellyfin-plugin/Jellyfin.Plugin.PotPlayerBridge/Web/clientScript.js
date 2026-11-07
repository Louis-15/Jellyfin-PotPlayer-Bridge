/*
 * Jellyfin → PotPlayer 桥接：注入到 jellyfin-web 的客户端脚本。
 *
 * 由服务器插件在 /web/index.html 的 </body> 前插入 <script defer> 加载，
 * 实际内容由 /PotPlayerBridge/Client.js 提供（服务端会把配置占位符替换成当前生效的 JSON）。
 *
 * M2 的职责：
 *   1. 打印版本，确认脚本确实被注入并且能执行；
 *   2. 等 window.ApiClient 就绪，确认拿到了已登录的 Jellyfin 会话；
 *   3. 把配置挂到 window.PotPlayerBridge 上，并加载季配置面板模块。
 * 接管播放按钮、调用本机代理从 M3 / M4 开始，这一版仍然不拦截任何点击，
 * 以免在半成品状态下改坏 Jellyfin 原有的播放行为。
 */
(function () {
    'use strict';

    // 插件重复注入或页面被缓存时，保证脚本只执行一次。
    if (window.__potPlayerBridgeClient) {
        return;
    }

    var CONFIG = __PPB_CONFIG_JSON__;
    var SCRIPT_SRC = (document.currentScript && document.currentScript.src) || '';
    var CACHE_STAMP = (function () {
        var match = /[?&]v=([^&]+)/.exec(SCRIPT_SRC);
        return match ? decodeURIComponent(match[1]) : '';
    })();

    window.__potPlayerBridgeClient = true;

    function log(message, detail) {
        if (typeof detail === 'undefined') {
            console.log('[PotPlayerBridge] ' + message);
        } else {
            console.log('[PotPlayerBridge] ' + message, detail);
        }
    }

    // ---------- 与 Jellyfin 插件接口通信 ----------
    // 放在 Client.js 而不是面板里：面板只负责 UI，
    // 后续接管播放按钮时也要用同一套地址/鉴权，避免两处实现漂移。

    function scriptBase() {
        var index = SCRIPT_SRC.indexOf('/PotPlayerBridge/');
        return index >= 0 ? SCRIPT_SRC.substring(0, index) : '';
    }

    function queryString(params) {
        var parts = [];
        for (var key in params) {
            if (Object.prototype.hasOwnProperty.call(params, key)) {
                parts.push(encodeURIComponent(key) + '=' + encodeURIComponent(params[key]));
            }
        }

        return parts.length ? '?' + parts.join('&') : '';
    }

    function apiUrl(name, params) {
        try {
            if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
                return window.ApiClient.getUrl('PotPlayerBridge/' + name, params);
            }
        } catch (error) {
            // 老版本 ApiClient 可能没有 getUrl；退回脚本自身地址推导。
        }

        var base = scriptBase();
        return base ? base + '/PotPlayerBridge/' + name + queryString(params || {}) : null;
    }

    function accessToken() {
        try {
            if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
                var token = window.ApiClient.accessToken();
                if (token) {
                    return token;
                }
            }
        } catch (error) {
            // 继续走 localStorage 兜底。
        }

        try {
            var raw = window.localStorage.getItem('jellyfin_credentials');
            var data = raw ? JSON.parse(raw) : null;
            var servers = data && data.Servers;
            return servers && servers.length ? servers[0].AccessToken : null;
        } catch (error) {
            return null;
        }
    }

    // 注意：本服务器只认 Authorization 头，X-Emby-Token 会 401（12.2.0 实测）。
    function requestJson(name, params, method, body) {
        var url = apiUrl(name, params);
        if (!url) {
            return Promise.reject(new Error('无法构造接口地址'));
        }

        var headers = { Accept: 'application/json' };
        var token = accessToken();
        if (token) {
            headers.Authorization = 'MediaBrowser Token="' + token + '"';
        }

        if (body) {
            headers['Content-Type'] = 'application/json';
        }

        return fetch(url, {
            method: method || 'GET',
            headers: headers,
            credentials: 'same-origin',
            cache: 'no-store',
            body: body ? JSON.stringify(body) : undefined
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
                        : '接口返回 HTTP ' + response.status;
                    if (response.status === 401) {
                        message = '登录状态已失效，请刷新页面后重新登录。';
                    }

                    throw new Error(message);
                }

                return payload;
            });
        });
    }

    window.PotPlayerBridge = {
        version: CONFIG.version,
        config: CONFIG,
        ready: false,
        api: {
            url: apiUrl,
            token: accessToken,
            get: function (name, params) {
                return requestJson(name, params, 'GET', null);
            },
            post: function (name, body) {
                return requestJson(name, null, 'POST', body);
            }
        }
    };

    function loadSeasonPanel() {
        if (window.__ppbSeasonPanelRequested) {
            return;
        }

        window.__ppbSeasonPanelRequested = true;
        loadModuleScript('SeasonPanel.js', '季配置面板');
    }

    function loadPlayClient() {
        if (window.__ppbPlayClientRequested) {
            return;
        }

        window.__ppbPlayClientRequested = true;
        loadModuleScript('PlayClient.js', '播放按钮接管');
    }

    function loadProgressBadge() {
        if (window.__ppbProgressBadgeRequested) {
            return;
        }

        window.__ppbProgressBadgeRequested = true;
        loadModuleScript('ProgressBadge.js', '卡片进度角标');
    }

    function loadModuleScript(fileName, label) {
        // 优先用 ApiClient.getUrl（能正确处理反向代理子路径），失败时按脚本自身地址推导。
        var url = null;
        try {
            url = window.ApiClient.getUrl('PotPlayerBridge/' + fileName);
        } catch (error) {
            url = null;
        }

        if (!url && SCRIPT_SRC) {
            url = SCRIPT_SRC.replace(/\/Client\.js(\?.*)?$/i, '/' + fileName);
        }

        if (!url) {
            log('无法确定' + label + '脚本地址，跳过加载。');
            return;
        }

        // 把 Client.js 自己的版本参数带给子脚本：否则插件更新后，
        // 浏览器仍会用缓存里的旧 SeasonPanel.js / PlayClient.js。
        var version = CACHE_STAMP;
        if (version) {
            url += (url.indexOf('?') >= 0 ? '&' : '?') + 'v=' + encodeURIComponent(version);
        }

        var script = document.createElement('script');
        script.src = url;
        script.defer = true;
        script.onerror = function () {
            log(label + '脚本加载失败：' + url);
        };

        document.head.appendChild(script);
    }

    log('客户端脚本已加载，版本 ' + CONFIG.version);

    // ApiClient 由 jellyfin-web 初始化后挂到 window 上，脚本执行时机不保证在它之后，
    // 所以这里轮询等待；登录页没有 ApiClient 时只提示一次，绝不做任何阻断操作。
    var waited = 0;
    var timer = window.setInterval(function () {
        waited += 500;

        if (window.ApiClient) {
            window.clearInterval(timer);
            window.PotPlayerBridge.ready = true;
            log('已连接到 Jellyfin Web（ApiClient 就绪），加载季配置面板与播放接管。');
            loadSeasonPanel();
            loadPlayClient();
            loadProgressBadge();
            return;
        }

        if (waited >= CONFIG.startTimeoutMs) {
            window.clearInterval(timer);
            log('没有等到 window.ApiClient（可能停留在登录页），脚本保持待命。');
        }
    }, 500);
})();
