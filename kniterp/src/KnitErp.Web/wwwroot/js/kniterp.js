// knitERP: подписи столбцов и раскрытие строк таблиц на телефоне, горячие клавиши и фокус панели инструментов.
// Отдельным файлом, а не в разметке: политика безопасности (CSP) запрещает встроенные скрипты.
// Подписи столбцов для карточного вида таблиц на телефоне: data-label ячейки = текст заголовка её столбца.
// Blazor перерисовывает строки, поэтому подписи ставятся заново при каждом изменении страницы.
(function () {
    function label() {
        document.querySelectorAll('.scroll table:not(.matrix)').forEach(function (table) {
            var heads = Array.prototype.map.call(table.querySelectorAll('thead th'), function (th) { return th.textContent.trim(); });
            if (heads.length === 0) return;
            table.querySelectorAll('tbody tr').forEach(function (tr) {
                var col = 0;
                Array.prototype.forEach.call(tr.children, function (td) {
                    var span = td.colSpan || 1;
                    var text = span === 1 ? heads[col] : '';
                    if (text && td.getAttribute('data-label') !== text) td.setAttribute('data-label', text);
                    col += span;
                });
            });
        });
    }
    // Второстепенные столбцы (th.opt): ячейки помечаются data-opt, строка получает «Подробнее» на телефоне.
    function priority() {
        document.querySelectorAll('.scroll table:not(.matrix)').forEach(function (table) {
            var opt = Array.prototype.map.call(table.querySelectorAll('thead th'), function (th) { return th.classList.contains('opt'); });
            if (opt.indexOf(true) < 0) return;
            table.querySelectorAll('tbody tr').forEach(function (tr) {
                var col = 0, has = false;
                Array.prototype.forEach.call(tr.children, function (td) {
                    var span = td.colSpan || 1;
                    if (span === 1 && opt[col]) { td.setAttribute('data-opt', ''); has = true; }
                    col += span;
                });
                if (has && !tr.hasAttribute('data-has-opt')) {
                    tr.setAttribute('data-has-opt', '');
                    // D810: без aria-expanded — у строки обычной таблицы он недопустим (axe); скрытые ячейки читает скринридер.
                    if (window.matchMedia('(max-width: 599px)').matches) tr.setAttribute('tabindex', '0');
                }
            });
        });
    }
    // D810: таблица шире своей области прокручивается — область доступна с клавиатуры (стрелки) и подписана для скринридера.
    function scrollable() {
        document.querySelectorAll('.scroll').forEach(function (box) {
            var wide = box.scrollWidth > box.clientWidth + 1;
            if (wide && !box.hasAttribute('tabindex')) {
                box.setAttribute('tabindex', '0');
                box.setAttribute('role', 'region');
                if (!box.hasAttribute('aria-label')) box.setAttribute('aria-label', document.documentElement.getAttribute('data-scroll-label') || 'Таблица');
            } else if (!wide && box.getAttribute('tabindex') === '0' && box.getAttribute('role') === 'region') {
                box.removeAttribute('tabindex'); box.removeAttribute('role');
            }
        });
    }
    // Аудит 12.10.2026: фильтры списков на телефоне свёрнуты при первом показе (FoldOnPhone), на компьютере открыты.
    function fold() {
        var phone = window.matchMedia('(max-width: 599px)').matches;
        document.querySelectorAll('details.fold-phone:not([data-init])').forEach(function (d) {
            d.setAttribute('data-init', '');
            if (phone) d.open = false;
        });
    }
    function toggleRow(tr) {
        if (!window.matchMedia('(max-width: 599px)').matches) return;
        var open = tr.hasAttribute('data-open');
        if (open) tr.removeAttribute('data-open'); else tr.setAttribute('data-open', '');
    }
    document.addEventListener('click', function (e) {
        var tr = e.target.closest && e.target.closest('tr[data-has-opt]');
        if (!tr || e.target.closest('a, button, input, select, textarea, label')) return;
        toggleRow(tr);
    });
    document.addEventListener('keydown', function (e) {
        if ((e.key === 'Enter' || e.key === ' ') && e.target.matches && e.target.matches('tr[data-has-opt]')) { e.preventDefault(); toggleRow(e.target); }
    });
    var pending = false;
    new MutationObserver(function () {
        if (pending) return;
        pending = true;
        requestAnimationFrame(function () { pending = false; label(); priority(); fold(); scrollable(); });
    }).observe(document.documentElement, { childList: true, subtree: true });
    document.addEventListener('DOMContentLoaded', function () { label(); priority(); fold(); scrollable(); });
    window.addEventListener('resize', function () { scrollable(); });
})();

// Панель быстрого доступа: горячие клавиши, фокус в панели инструмента и обратно, буфер обмена.
window.kniterp = {
    _returnFocus: null,
    registerHotkeys: function (dotnet) {
        // Повторный вызов (переподключение Blazor) заменяет прежний обработчик, а не добавляет второй со старой ссылкой.
        if (kniterp._onAction) document.removeEventListener('knit:action', kniterp._onAction);
        // D116: кнопка в панели персонажа помощника открывает чат помощника.
        kniterp._onAction = function () { dotnet.invokeMethodAsync('OnHotkey', 'assistant'); };
        document.addEventListener('knit:action', kniterp._onAction);
        kniterp.hotkeysReady = true;
        document.addEventListener('keydown', function (e) {
            if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K' || e.key === 'л' || e.key === 'Л')) {
                e.preventDefault();
                dotnet.invokeMethodAsync('OnHotkey', 'search');
            } else if (e.key === 'Escape' && document.querySelector('.drawer.open') && !(document.activeElement && document.activeElement.closest('.float-win'))) {
                dotnet.invokeMethodAsync('OnHotkey', 'escape');
            }
        });
    },
    focusDrawer: function (id) {
        if (!document.querySelector('.drawer.open:focus-within')) kniterp._returnFocus = document.activeElement;
        setTimeout(function () { var el = document.getElementById(id); if (el) el.focus(); }, 60);
    },
    restoreFocus: function () {
        var el = kniterp._returnFocus;
        kniterp._returnFocus = null;
        if (el && document.contains(el)) el.focus();
    },
    // Отдельное окно (калькулятор): место из прошлого раза, иначе — справа внизу; фокус в окно, чтобы сразу считать с клавиатуры.
    openWindow: function (id) {
        var win = document.getElementById(id);
        if (!win) return;
        try {
            var pos = JSON.parse(localStorage.getItem('kniterp.win.' + id) || 'null');
            if (pos) kniterp._place(win, pos.left, pos.top);
        } catch (e) { }
        // Клавиши калькулятора не уходят браузеру (Enter, «/» — быстрый поиск, Backspace, пробел).
        win.addEventListener('keydown', function (e) {
            if (e.ctrlKey || e.altKey || e.metaKey || e.target !== win) return;
            if (/^[0-9+\-*\/=%(),.:xхсcCСXХ]$/.test(e.key) || ['Enter', 'Backspace', 'Delete', 'Escape', ' '].indexOf(e.key) >= 0) e.preventDefault();
        });
        win.focus();
    },
    _place: function (win, left, top) {
        var maxLeft = Math.max(0, window.innerWidth - win.offsetWidth), maxTop = Math.max(0, window.innerHeight - 40);
        win.style.left = Math.min(Math.max(0, left), maxLeft) + 'px';
        win.style.top = Math.min(Math.max(0, top), maxTop) + 'px';
        win.style.right = 'auto';
        win.style.bottom = 'auto';
    },
    copy: async function (text) {
        try { await navigator.clipboard.writeText(text); return true; } catch (e) { return false; }
    },
    // D124: «Поделиться» на телефоне — системное меню (мессенджеры, почта). Нет поддержки или отмена — false.
    canShare: function () { return !!navigator.share; },
    share: async function (title, text, url) {
        if (!navigator.share) { return false; }
        try { await navigator.share({ title: title, text: text, url: url }); return true; } catch (e) { return false; }
    }
};

// Печатные формы: кнопка с data-print открывает печать браузера (встроенный onclick запрещён CSP).
document.addEventListener('click', function (e) {
    if (e.target.closest && e.target.closest('[data-print]')) { e.preventDefault(); window.print(); }
});

// Перетаскивание отдельного окна за заголовок (data-drag-handle); положение сохраняется в браузере.
document.addEventListener('pointerdown', function (e) {
    var handle = e.target.closest && e.target.closest('[data-drag-handle]');
    if (!handle || e.target.closest('button')) return;
    var win = handle.closest('.float-win');
    if (!win) return;
    var rect = win.getBoundingClientRect(), dx = e.clientX - rect.left, dy = e.clientY - rect.top;
    handle.setPointerCapture(e.pointerId);
    function move(ev) { kniterp._place(win, ev.clientX - dx, ev.clientY - dy); }
    function up() {
        handle.removeEventListener('pointermove', move);
        handle.removeEventListener('pointerup', up);
        try { localStorage.setItem('kniterp.win.' + win.id, JSON.stringify({ left: win.offsetLeft, top: win.offsetTop })); } catch (err) { }
        win.focus();
    }
    handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', up);
});

// Выбор темы в меню профиля (кнопки data-theme-choice): сразу применяется и запоминается в браузере.
function kniterpMarkTheme() {
    var current = document.documentElement.getAttribute('data-theme') || 'light';
    document.querySelectorAll('[data-theme-choice]').forEach(function (b) {
        var on = b.getAttribute('data-theme-choice') === current;
        b.classList.toggle('added', on);
        b.setAttribute('aria-checked', on ? 'true' : 'false');
    });
}
document.addEventListener('click', function (e) {
    var btn = e.target.closest && e.target.closest('[data-theme-choice]');
    if (!btn) return;
    var theme = btn.getAttribute('data-theme-choice');
    document.documentElement.setAttribute('data-theme', theme);
    try { localStorage.setItem('kniterp.theme', theme); } catch (err) { }
    kniterpMarkTheme();
});
// D114: размер интерфейса (кнопки data-density-choice в профиле) — так же, как тема: сразу и запоминается в браузере.
function kniterpMarkDensity() {
    var current = document.documentElement.getAttribute('data-density') || 'compact';
    document.querySelectorAll('[data-density-choice]').forEach(function (b) {
        var on = b.getAttribute('data-density-choice') === current;
        b.classList.toggle('added', on);
        b.setAttribute('aria-checked', on ? 'true' : 'false');
    });
}
document.addEventListener('click', function (e) {
    var btn = e.target.closest && e.target.closest('[data-density-choice]');
    if (!btn) return;
    var density = btn.getAttribute('data-density-choice');
    document.documentElement.setAttribute('data-density', density);
    try { localStorage.setItem('kniterp.density', density); } catch (err) { }
    kniterpMarkDensity();
});
// D116: тема персонажа помощника — как у приложения («как в системе» — по настройке системы).
function kniterpSyncMascot() {
    var el = document.querySelector('knit-assistant');
    if (!el) return;
    var t = document.documentElement.getAttribute('data-theme') || 'light';
    if (t === 'auto') t = window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
    if (el.getAttribute('theme') !== t) el.setAttribute('theme', t);
}
// Улучшенная навигация Blazor может переписать атрибуты <html> — тема и размер восстанавливаются из браузера.
new MutationObserver(function () {
    if (!document.documentElement.getAttribute('data-theme')) {
        var t = 'light';
        try { t = localStorage.getItem('kniterp.theme') || 'light'; } catch (err) { }
        document.documentElement.setAttribute('data-theme', t);
    }
    if (!document.documentElement.getAttribute('data-density')) {
        var d = 'compact';
        try { d = localStorage.getItem('kniterp.density') || 'compact'; } catch (err) { }
        document.documentElement.setAttribute('data-density', d);
    }
    kniterpMarkTheme();
    kniterpMarkDensity();
    kniterpSyncMascot();
}).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-theme', 'data-density'] });
