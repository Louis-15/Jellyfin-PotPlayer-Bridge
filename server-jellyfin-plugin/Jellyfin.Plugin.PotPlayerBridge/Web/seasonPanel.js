/*
 * 季配置面板：在「季详情页」和「单集详情页」插入片头/片尾秒数设置。
 *
 * 由 Client.js 在 ApiClient 就绪后动态加载（index.html 里只有一个注入标签）。
 * 关键约束：
 *   1. 详情页存在缓存 DOM（坑 #7），必须操作「当前可见」的 .itemDetailPage；
 *   2. 设置粒度是「季」——在单集页保存，改的是该集所属季，保存后对整季生效；
 *   3. 面板是纯附加 UI，读取/保存失败只在面板里提示，绝不影响 Jellyfin 原有功能。
 */
(function () {
    'use strict';

    if (window.__ppbSeasonPanel) {
        return;
    }

    window.__ppbSeasonPanel = true;

    var PANEL_CLASS = 'ppbPanel';
    var MAX_SECONDS = 7200;

    var state = {
        itemId: null,
        info: null,
        loading: false,
        sequence: 0,
        timer: null,
        cssRequested: false
    };

    function log(message, detail) {
        if (typeof detail === 'undefined') {
            console.log('[PotPlayerBridge] ' + message);
        } else {
            console.log('[PotPlayerBridge] ' + message, detail);
        }
    }

    // ---------- 接口访问 ----------
    // 地址拼接与鉴权统一由 Client.js 提供（window.PotPlayerBridge.api），
    // 这里只负责发什么请求、怎么把结果画出来。

    function api() {
        return window.PotPlayerBridge && window.PotPlayerBridge.api;
    }

    function ensureStyles() {
        if (state.cssRequested) {
            return;
        }

        state.cssRequested = true;
        var client = api();
        var href = client ? client.url('SeasonSettings.css') : null;
        if (!href) {
            return;
        }

        var link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = href;
        link.setAttribute('data-ppb', 'season-settings');
        document.head.appendChild(link);
    }

    // ---------- 页面定位 ----------

    function currentItemId() {
        // 详情页固定形如 #/details?id=<32位十六进制>（后面可能跟 serverId 等参数）。
        var match = /[?&]id=([0-9a-fA-F]{32})/.exec(window.location.hash || '');
        return match ? match[1].toLowerCase() : null;
    }

    function isVisible(element) {
        return !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
    }

    function visibleDetailPage() {
        var pages = document.querySelectorAll('.itemDetailPage');
        for (var i = 0; i < pages.length; i++) {
            if (isVisible(pages[i])) {
                return pages[i];
            }
        }

        return null;
    }

    function removePanels() {
        var panels = document.querySelectorAll('.' + PANEL_CLASS);
        for (var i = 0; i < panels.length; i++) {
            panels[i].parentNode.removeChild(panels[i]);
        }
    }

    // ---------- 面板构建 ----------

    function buildField(labelText, value) {
        var label = document.createElement('label');
        label.className = 'ppbField';

        var caption = document.createElement('span');
        caption.className = 'ppbFieldLabel';
        caption.textContent = labelText;

        var input = document.createElement('input');
        input.className = 'ppbInput';
        input.type = 'number';
        input.min = '0';
        input.max = String(MAX_SECONDS);
        input.step = '1';
        input.inputMode = 'numeric';
        input.value = String(value || 0);

        label.appendChild(caption);
        label.appendChild(input);
        return { label: label, input: input };
    }

    function buildPanel(info) {
        var panel = document.createElement('div');
        panel.className = PANEL_CLASS;

        var head = document.createElement('div');
        head.className = 'ppbHead';

        var title = document.createElement('span');
        title.className = 'ppbTitle';
        title.textContent = 'PotPlayer 片头片尾';

        var target = document.createElement('span');
        target.className = 'ppbTarget';
        target.textContent = info.itemType === 'Episode'
            ? '所属季：' + (info.seasonName || '未知') + '（保存后对整季生效）'
            : '本季：' + (info.seasonName || info.name || '未知');

        head.appendChild(title);
        head.appendChild(target);

        var row = document.createElement('div');
        row.className = 'ppbRow';

        var intro = buildField('片头（秒）', info.introSeconds);
        var outro = buildField('片尾（秒）', info.outroSeconds);

        var saveButton = document.createElement('button');
        saveButton.type = 'button';
        saveButton.className = 'emby-button raised ppbSave';
        saveButton.textContent = '保存';

        var status = document.createElement('span');
        status.className = 'ppbStatus';
        status.textContent = '';

        saveButton.addEventListener('click', function () {
            save(info, intro.input, outro.input, saveButton, status);
        });

        row.appendChild(intro.label);
        row.appendChild(outro.label);
        row.appendChild(saveButton);
        row.appendChild(status);

        var hint = document.createElement('div');
        hint.className = 'ppbHint';
        hint.textContent = '0 表示不跳过。片头只在「起播位置还没超过片头」时跳，避免续播时往回跳；设置保存后从下一次播放开始生效。';

        panel.appendChild(head);
        panel.appendChild(row);
        panel.appendChild(hint);
        return panel;
    }

    function mount(page, info) {
        // 页面可能正处在 React 切换/重建的瞬间；此时挂到隐藏页上会看起来「闪一下就不见了」。
        // 交给下一轮 check 用缓存重挂载。
        if (!page || !isVisible(page)) {
            scheduleCheck();
            return;
        }

        var panel = buildPanel(info);
        ensureStyles();

        // 位置：插在标题/按钮行（.detailRibbon）之后，并让背景从海报右边缘一直铺到页面右边缘。
        // - 不能插在 .detailPagePrimaryContainer 之后：季页的 primary 包含整季剧集列表（8000+px），
        //   面板会被排到页面最底部；
        // - 也不能横跨整页插在 ribbon 后面：那会盖住左侧海报。
        // 具体左右偏移在 alignPanel 里按「海报右边缘 + 标题栏的文字边距」实时计算。
        var anchor = page.querySelector('.detailRibbon')
                     || page.querySelector('.detailPagePrimaryContainer');

        if (!anchor || !anchor.parentElement) {
            // 页面结构还没渲染完：交给 MutationObserver / 巡检重试。
            return;
        }

        removePanels();
        anchor.insertAdjacentElement('afterend', panel);
        alignPanel(panel, page);
        log('已插入季配置面板：' + (info.seasonName || info.name || info.itemId));
    }

    /*
     * 让面板与标题栏对齐：
     *   背景从页面左边缘一直铺到右边缘（与标题栏横幅一致，海报会盖在它上面）；
     *   文字左边距 = 标题栏的 padding-left，右边距 = 标题栏的 padding-right。
     * 不同窗口宽度下海报/标题栏边距都会变，所以按当下布局实时计算，不写死像素。
     */
    function alignPanel(panel, page) {
        var ribbon = page.querySelector('.detailRibbon');
        var ribbonStyle = ribbon ? window.getComputedStyle(ribbon) : null;
        var padLeft = ribbonStyle ? (parseFloat(ribbonStyle.paddingLeft) || 0) : 0;
        var padRight = ribbonStyle ? (parseFloat(ribbonStyle.paddingRight) || 0) : 0;

        // 海报是定位元素，会自然盖在横幅上方；文字从标题栏的左边距开始，所以不会被海报挡住。
        panel.style.marginLeft = '0px';
        panel.style.paddingLeft = Math.round(padLeft) + 'px';
        panel.style.paddingRight = Math.round(padRight) + 'px';
    }

    function realign() {
        var page = visibleDetailPage();
        var panel = page && page.querySelector('.' + PANEL_CLASS);
        if (page && panel) {
            alignPanel(panel, page);
        }
    }

    function save(info, introInput, outroInput, button, status) {
        var intro = Number(introInput.value);
        var outro = Number(outroInput.value);

        if (!isValidSeconds(intro) || !isValidSeconds(outro)) {
            status.textContent = '秒数需为 0～' + MAX_SECONDS + ' 的整数';
            status.className = 'ppbStatus ppbStatusError';
            return;
        }

        var client = api();
        if (!client) {
            status.className = 'ppbStatus ppbStatusError';
            status.textContent = '保存失败：接口未就绪，请刷新页面后重试';
            return;
        }

        button.disabled = true;
        status.className = 'ppbStatus';
        status.textContent = '保存中…';

        client.post('SeasonSettings', {
            itemId: info.itemId,
            introSeconds: intro,
            outroSeconds: outro
        }).then(function (data) {
            button.disabled = false;
            status.textContent = '已保存，下次播放生效';
            log('季配置已保存：' + (data.seasonName || data.seasonId) +
                ' 片头 ' + data.introSeconds + 's / 片尾 ' + data.outroSeconds + 's');
        }, function (error) {
            button.disabled = false;
            status.className = 'ppbStatus ppbStatusError';
            status.textContent = '保存失败：' + error.message;
        });
    }

    function isValidSeconds(value) {
        return window.Number.isInteger(value) && value >= 0 && value <= MAX_SECONDS;
    }

    // ---------- 生命周期 ----------

    function check() {
        var id = currentItemId();

        if (!id) {
            // 真正离开详情页：清缓存、清面板（Jellyfin 是 SPA，旧页面 DOM 会被缓存）。
            state.itemId = null;
            state.info = null;
            state.loading = false;
            removePanels();
            return;
        }

        var page = visibleDetailPage();
        if (!page) {
            // 路由切换/React 重建时，会出现「一瞬间没有可见详情页」。
            // 这时绝不能删面板，否则用户看到的就是「闪一下就消失」；等下一轮再试。
            scheduleCheck();
            return;
        }

        if (state.itemId !== id) {
            // 切换到了另一项：旧缓存与旧面板都不再适用。
            state.itemId = id;
            state.info = null;
            state.loading = false;
            removePanels();
        }

        if (page.querySelector('.' + PANEL_CLASS)) {
            // 已存在时也重新对齐一次：窗口缩放后 Jellyfin 会重排海报列，
            // 尺寸变化不一定伴随 DOM 重建，这里顺手校正，2 秒巡检也覆盖得到。
            alignPanel(page.querySelector('.' + PANEL_CLASS), page);
            return;
        }

        // 已经拿到过这一项的配置：直接重建，不再请求接口。
        // 这样 React 因为图片懒加载等原因重建 DOM 后，面板能立刻恢复，不会长期消失。
        if (state.info && state.info.itemId === id) {
            if (state.info.canConfigure) {
                mount(page, state.info);
            }

            return;
        }

        if (state.loading) {
            return; // 请求还在飞，响应回来后会挂载
        }

        load(id);
    }

    function load(id) {
        var client = api();
        if (!client) {
            log('接口未就绪，跳过季配置面板加载。');
            return;
        }

        var sequence = ++state.sequence;
        state.loading = true;

        client.get('ItemInfo', { itemId: id }).then(function (info) {
            if (sequence !== state.sequence || state.itemId !== id) {
                return; // 用户已经切到别的页面
            }

            state.loading = false;
            state.info = info || null;

            if (!info || !info.canConfigure) {
                removePanels();
                return;
            }

            var page = visibleDetailPage();
            if (!page) {
                scheduleCheck();
                return;
            }

            mount(page, info);
        }, function (error) {
            if (sequence !== state.sequence || state.itemId !== id) {
                return;
            }

            state.loading = false;
            removePanels();
            log('读取条目信息失败（不显示季配置面板）：' + error.message);
        });
    }

    function scheduleCheck() {
        if (state.timer) {
            return;
        }

        state.timer = window.setTimeout(function () {
            state.timer = null;
            check();
        }, 200);
    }

    function start() {
        if (!document.body) {
            window.setTimeout(start, 300);
            return;
        }

        var observer = new MutationObserver(scheduleCheck);
        observer.observe(document.body, { childList: true, subtree: true });
        window.addEventListener('hashchange', scheduleCheck);

        // 窗口尺寸变化时 Jellyfin 会重新排海报列，面板的左右偏移要跟着重算。
        // 排布是异步的（尤其是海报），所以在 150ms / 600ms 各校正一次；2 秒巡检还会兜底。
        window.addEventListener('resize', function () {
            window.setTimeout(realign, 150);
            window.setTimeout(realign, 600);
        });

        // 兜底巡检：React 重建详情页时如果 MutationObserver 恰好没覆盖到我们关心的节点，
        // 这 2 秒一次的轻量检查（只查 DOM，不请求接口）能把面板补回来。
        window.setInterval(function () {
            if (currentItemId()) {
                scheduleCheck();
            }
        }, 2000);

        scheduleCheck();
        log('季配置面板已就绪');
    }

    start();
})();
