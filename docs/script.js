// RambiBox website: theme toggle, screenshot lightbox, placeholders for missing screenshots. No dependencies.
(function () {
  "use strict";
  var root = document.documentElement;

  // ---- theme toggle (remembers the choice; falls back to the system setting)
  var toggle = document.querySelector(".theme-toggle");
  if (toggle) {
    toggle.addEventListener("click", function () {
      var current = root.dataset.theme ||
        (window.matchMedia && window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark");
      var next = current === "light" ? "dark" : "light";
      root.dataset.theme = next;
      try { localStorage.setItem("rambibox-theme", next); } catch (e) { /* storage unavailable */ }
    });
  }

  // ---- phone screenshots that are not there (yet): keep the styled placeholder
  function markMissing(img) {
    var fig = img.closest(".phone");
    if (fig) fig.classList.add("is-missing");
    var link = img.closest("a");
    if (link) link.removeAttribute("data-lightbox");
  }
  document.querySelectorAll("img.phone-img").forEach(function (img) {
    if (img.complete && img.naturalWidth === 0) markMissing(img);
    img.addEventListener("error", function () { markMissing(img); });
  });

  // ---- lightbox (links still open the image directly without JavaScript)
  var dialog = document.getElementById("lightbox");
  var big = document.getElementById("lightbox-img");
  var caption = document.getElementById("lightbox-caption");
  if (dialog && typeof dialog.showModal === "function") {
    document.addEventListener("click", function (e) {
      var link = e.target.closest && e.target.closest("a[data-lightbox]");
      if (!link || e.ctrlKey || e.metaKey || e.shiftKey) return;
      var img = link.querySelector("img");
      if (link.closest(".phone.is-missing")) { e.preventDefault(); return; }
      e.preventDefault();
      big.src = link.getAttribute("href");
      big.alt = img ? img.alt : "";
      var fig = link.closest("figure");
      var cap = fig && fig.querySelector("figcaption");
      caption.textContent = cap ? cap.textContent : "";
      dialog.showModal();
    });
    dialog.addEventListener("click", function (e) { if (e.target === dialog) dialog.close(); });
    dialog.addEventListener("close", function () { big.removeAttribute("src"); });
  }
})();
