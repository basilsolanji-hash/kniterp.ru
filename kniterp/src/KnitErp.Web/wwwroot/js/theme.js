// knitERP: тема до отрисовки страницы (подключается в <head>, чтобы не было вспышки тёмной темы).
// Выбор — в профиле: тёмная, светлая, как в системе; хранится в этом браузере.
(function () {
    var theme = 'dark';
    try { theme = localStorage.getItem('kniterp.theme') || 'dark'; } catch (e) { }
    if (['dark', 'light', 'auto'].indexOf(theme) < 0) theme = 'dark';
    document.documentElement.setAttribute('data-theme', theme);
    // D114: размер интерфейса — компактно (по умолчанию), обычно, крупно.
    var density = 'compact';
    try { density = localStorage.getItem('kniterp.density') || 'compact'; } catch (e) { }
    if (['compact', 'normal', 'large'].indexOf(density) < 0) density = 'compact';
    document.documentElement.setAttribute('data-density', density);
})();
