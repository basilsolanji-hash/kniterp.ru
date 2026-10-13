(function () {
  var burger = document.getElementById('burger');
  var nav = document.getElementById('nav');
  var searchToggle = document.getElementById('searchToggle');
  var search = document.getElementById('search');

  burger.addEventListener('click', function () {
    var open = nav.classList.toggle('is-open');
    burger.setAttribute('aria-expanded', open ? 'true' : 'false');
  });
  nav.addEventListener('click', function (e) {
    if (e.target.tagName === 'A') {
      nav.classList.remove('is-open');
      burger.setAttribute('aria-expanded', 'false');
    }
  });

  searchToggle.addEventListener('click', function () {
    search.hidden = !search.hidden;
    if (!search.hidden) search.querySelector('input[type=search]').focus();
  });

  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
      search.hidden = true;
      nav.classList.remove('is-open');
      burger.setAttribute('aria-expanded', 'false');
    }
  });
})();
