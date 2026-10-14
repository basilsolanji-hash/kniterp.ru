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
    // D812 (запрос владельца 13.10, п. 3): фильтр списка применяется сразу при изменении поля — без кнопки «Показать».
    // Отправка — с паузой 0,3 с после последнего изменения (несколько выборов подряд — одна загрузка); текст — по Enter или уходу из поля.
    document.addEventListener('change', function (e) {
        var form = e.target.closest && e.target.closest('form[data-autosubmit]');
        if (!form) return;
        clearTimeout(form._knitApply);
        form._knitApply = setTimeout(function () { if (form.isConnected) form.requestSubmit(); }, 300);
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

// ===== Раздел 0 ТЗ интерфейса, п. 6–7 (D816) =====
(function () {
    var phone = function () { return window.matchMedia('(max-width: 599px)').matches; };
    var interactive = 'a, button, input, select, textarea, label, summary, details, [role="button"], [contenteditable]';

    // П. 6: объект открывается нажатием в любом месте строки. Ссылка на карточку — первая ссылка в первых двух ячейках строки
    // (номер или название); строка помечается data-row-link (курсор-рука). На телефоне строка с «Подробнее» по-прежнему раскрывается.
    function rowLink(tr) {
        var cells = Array.prototype.slice.call(tr.children, 0, 2);
        for (var i = 0; i < cells.length; i++) {
            var a = cells[i].querySelector('a[href]:not([target]):not([download])');
            if (a && !a.closest('.row-actions')) return a;
        }
        return null;
    }
    function markRows() {
        document.querySelectorAll('main .scroll table:not(.matrix) tbody tr').forEach(function (tr) {
            var has = !!rowLink(tr);
            if (has !== tr.hasAttribute('data-row-link')) {
                if (has) tr.setAttribute('data-row-link', ''); else tr.removeAttribute('data-row-link');
            }
        });
    }
    document.addEventListener('click', function (e) {
        if (e.button !== 0 || !e.target.closest) return;
        var tr = e.target.closest('tr[data-row-link]');
        if (!tr || e.target.closest(interactive)) return;
        if (tr.hasAttribute('data-has-opt') && phone()) return; // на телефоне нажатие раскрывает строку
        var sel = window.getSelection && window.getSelection().toString();
        if (sel) return; // выделяли текст — не переходим
        var a = rowLink(tr);
        if (!a) return;
        if (e.ctrlKey || e.metaKey || e.shiftKey) { window.open(a.href, '_blank', 'noopener'); return; }
        a.click(); // переход через ссылку — его перехватывает маршрутизатор Blazor
    });
    document.addEventListener('auxclick', function (e) {
        if (e.button !== 1 || !e.target.closest) return;
        var tr = e.target.closest('tr[data-row-link]');
        if (!tr || e.target.closest(interactive)) return;
        var a = rowLink(tr);
        if (a) { e.preventDefault(); window.open(a.href, '_blank', 'noopener'); }
    });

    // П. 7: несохранённые изменения. Форма с основной кнопкой отправки (не фильтр) после ввода — «грязная», пока её не отправили
    // или она не исчезла со страницы. Уход внутри программы спрашивает MainLayout (NavigationLock), закрытие вкладки — браузер.
    var dirty = null;
    function primarySubmit(form) { return form.querySelector('button[type="submit"]:not(.secondary):not(.filter-apply):not(.link)'); }
    function trackedForm(el) {
        var form = el.closest && el.closest('main form:not([data-autosubmit]):not([data-no-dirty])');
        return form && primarySubmit(form) ? form : null;
    }
    document.addEventListener('input', function (e) { var f = trackedForm(e.target); if (f) dirty = f; });
    document.addEventListener('submit', function (e) { if (e.target === dirty) dirty = null; }, true);
    function isDirty() { if (dirty && !dirty.isConnected) dirty = null; return !!dirty; }
    window.addEventListener('beforeunload', function (e) { if (isDirty()) { e.preventDefault(); e.returnValue = ''; } });

    // П. 7: Ctrl+S — сохранить: основная кнопка формы, в которой фокус; иначе единственная такая форма на странице.
    document.addEventListener('keydown', function (e) {
        if (!(e.ctrlKey || e.metaKey) || !(e.key === 's' || e.key === 'S' || e.key === 'ы' || e.key === 'Ы')) return;
        var form = document.activeElement && document.activeElement.closest && document.activeElement.closest('main form');
        if (!form || !primarySubmit(form)) {
            var forms = Array.prototype.filter.call(document.querySelectorAll('main form:not([data-autosubmit])'), function (f) {
                var b = primarySubmit(f); return b && b.offsetParent !== null;
            });
            form = forms.length === 1 ? forms[0] : null;
        }
        if (!form) return;
        e.preventDefault();
        var btn = primarySubmit(form);
        if (!btn || btn.disabled) return;
        // Поле с фокусом отдаёт значение по событию change — без него форма ушла бы со старым значением.
        var active = document.activeElement;
        if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA' || active.tagName === 'SELECT')) {
            active.dispatchEvent(new Event('change', { bubbles: true }));
        }
        setTimeout(function () { if (form.isConnected) form.requestSubmit(btn); }, 0);
    });

    // Фильтры НЕ восстанавливаются скриптом: подстановка значений с применением во время загрузки страницы запускала второй
    // запрос к базе параллельно первому (у вкладки один DbContext — схема Blazor падает). Запоминание фильтров — через адрес
    // страницы (параметры запроса), по экранам (D816).

    // Чек-лист 4.1 (D819): открытая пользователем форма — курсор сразу в первом поле. Только мышь/клавиатура (на телефоне фокус
    // открыл бы клавиатуру) и не если пользователь уже вводит текст в другом поле.
    var seenForms = new WeakSet(), userAt = 0, userPath = '';
    // Форма открыта пользователем, если появилась вскоре после его нажатия; догрузившиеся со страницей формы фокус не забирают.
    document.addEventListener('pointerdown', function () { userAt = Date.now(); userPath = location.pathname; }, true);
    document.addEventListener('keydown', function (e) { if (e.key === 'Enter' || e.key === ' ') { userAt = Date.now(); userPath = location.pathname; } }, true);
    function autofocus() {
        var byUser = Date.now() - userAt < 1500 && userPath === location.pathname; // переход на другую страницу — не «открыл форму»
        document.querySelectorAll('main form').forEach(function (form) {
            if (seenForms.has(form)) return;
            seenForms.add(form);
            if (!byUser || form.hasAttribute('data-autosubmit') || !primarySubmit(form)) return;
            if (!window.matchMedia('(pointer: fine)').matches) return;
            var active = document.activeElement;
            // Не мешаем набору текста; флажок или кнопка в фокусе не в счёт.
            if (active && active.matches && active.matches('input:not([type=checkbox]):not([type=radio]):not([type=button]):not([type=submit]), select, textarea, [contenteditable]')) return;
            var first = Array.prototype.find.call(form.querySelectorAll('input, select, textarea'), function (el) {
                return !el.disabled && !el.readOnly && el.type !== 'hidden' && el.type !== 'checkbox' && el.type !== 'radio' && el.offsetParent !== null;
            });
            if (first) first.focus({ preventScroll: true });
        });
    }

    // Чек-лист 4.1 (D822): ошибка — у поля. Сообщение сервера «ИНН: 10 цифр…» или «Допуск — от 0 до 10%» начинается с названия поля:
    // поле с такой подписью подсвечивается (aria-invalid), связывается с сообщением (aria-describedby) и получает курсор.
    // Только точное совпадение подписи — иначе ничего не делаем. Подсветка снимается, как только поле изменили или сообщение исчезло.
    var errorSeq = 0;
    function ownLabelText(label) {
        var t = '';
        label.childNodes.forEach(function (n) { if (n.nodeType === 3) t += n.textContent; });
        return t.replace(/\s+/g, ' ').trim().toLowerCase();
    }
    function markErrors() {
        document.querySelectorAll('[aria-invalid="true"][data-knit-invalid]').forEach(function (el) {
            var msg = document.getElementById(el.getAttribute('data-knit-invalid'));
            if (!msg || !msg.isConnected || !msg.textContent.trim()) {
                el.removeAttribute('aria-invalid'); el.removeAttribute('data-knit-invalid'); el.removeAttribute('aria-describedby');
            }
        });
        document.querySelectorAll('main [role="alert"], main .error, main .flash.error').forEach(function (msg) {
            var text = msg.textContent.replace(/\s+/g, ' ').trim();
            if (!text || msg.getAttribute('data-knit-checked') === text) return;
            msg.setAttribute('data-knit-checked', text);
            var m = /^([^:—]{1,40}?)\s*(:| —)/.exec(text);
            if (!m) return;
            var name = m[1].toLowerCase();
            var scope = msg.closest('form') || msg.closest('.panel') || document.querySelector('main');
            var label = Array.prototype.find.call(scope.querySelectorAll('label'), function (l) { return ownLabelText(l) === name; });
            if (!label) return;
            var field = label.control || label.querySelector('input, select, textarea');
            if (!field || field.disabled) return;
            if (!msg.id) msg.id = 'knit-err-' + (++errorSeq);
            field.setAttribute('aria-invalid', 'true');
            field.setAttribute('data-knit-invalid', msg.id);
            field.setAttribute('aria-describedby', msg.id);
            field.focus({ preventScroll: false });
        });
    }
    document.addEventListener('input', function (e) {
        if (e.target.getAttribute && e.target.getAttribute('data-knit-invalid')) {
            e.target.removeAttribute('aria-invalid'); e.target.removeAttribute('data-knit-invalid'); e.target.removeAttribute('aria-describedby');
        }
    });

    var queued = false;
    new MutationObserver(function () {
        if (queued) return;
        queued = true;
        requestAnimationFrame(function () { queued = false; markRows(); autofocus(); markErrors(); });
    }).observe(document.documentElement, { childList: true, subtree: true });

    window.kniterp = window.kniterp || {};
    // D817: фильтр списка — в адресе страницы (без перехода и без повторной загрузки): «Назад» и обновление открывают тот же
    // отбор, ссылку можно отправить коллеге. Пустые значения из адреса убираются; параметры, которых нет в списке, не трогаются.
    window.kniterp.setQuery = function (values) {
        try {
            var url = new URL(location.href);
            Object.keys(values).forEach(function (k) {
                var v = values[k];
                if (v === null || v === undefined || v === '') url.searchParams.delete(k); else url.searchParams.set(k, v);
            });
            if (url.href !== location.href) history.replaceState(history.state, '', url.href);
        } catch (e) { }
    };
    window.kniterp.isDirty = isDirty;
    window.kniterp.clearDirty = function () { dirty = null; };
    window.kniterp.confirmLeave = function (text) { return window.confirm(text); };
})();
