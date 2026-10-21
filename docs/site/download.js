// The download button fetches the newest release's zip straight away. GitHub's /releases/latest skips pre-releases
// (every beta is one), so the newest release is read from the API; if that fails, the button keeps its link to the
// releases page.
(function () {
  var btn = document.getElementById('download');
  if (!btn || !window.fetch) return;
  fetch('https://api.github.com/repos/ldo2x/2XPing/releases?per_page=10', { headers: { Accept: 'application/vnd.github+json' } })
    .then(function (r) { return r.ok ? r.json() : null; })
    .then(function (list) {
      if (!list) return;
      for (var i = 0; i < list.length; i++) {
        var rel = list[i];
        if (rel.draft) continue;
        for (var j = 0; j < rel.assets.length; j++) {
          var a = rel.assets[j];
          if (/\.zip$/i.test(a.name)) {
            var v = rel.tag_name.replace(/^v/, '').replace(/-beta$/, '');
            btn.href = a.browser_download_url;
            btn.textContent = (btn.getAttribute('data-label') || 'Download') + ' ' + v;
            return;
          }
        }
      }
    })
    .catch(function () {});
})();
