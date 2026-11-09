/**
 * IgnisDisc Hardware Datasheet behaviour: release links, copy buttons, and GUI/CLI pane switcher.
 * Edit SITE_CONFIG below (or run scripts/Publish-ReleaseAndSite.ps1) to update links.
 */
const SITE_CONFIG = {
  version: "1.10",
  githubRepoUrl: "https://github.com/tether-works/IgnisDisc",
  githubReleasesUrl: "https://github.com/tether-works/IgnisDisc/releases",
  setupDownloadUrl: "https://github.com/tether-works/IgnisDisc/releases/download/v1.10/IgnisDisc-Setup.exe",
  portableDownloadUrl: "https://github.com/tether-works/IgnisDisc/releases/download/v1.10/IgnisDisc-Portable-win-x64.zip"
};

document.addEventListener("DOMContentLoaded", () => {
  const setHref = (id, url) => {
    const el = document.getElementById(id);
    if (el && url) el.setAttribute("href", url);
  };

  setHref("navGithubLink", SITE_CONFIG.githubRepoUrl);
  setHref("ctaGithub", SITE_CONFIG.githubReleasesUrl);
  setHref("btnDownloadSetup", SITE_CONFIG.setupDownloadUrl);
  setHref("btnDownloadPortable", SITE_CONFIG.portableDownloadUrl);

  const verEl = document.getElementById("navVersion");
  if (verEl && SITE_CONFIG.version) verEl.textContent = `v${SITE_CONFIG.version}`;

  document.querySelectorAll(".btn-copy").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const text = btn.getAttribute("data-copy") || "";
      try {
        await navigator.clipboard.writeText(text);
        const orig = btn.textContent;
        btn.textContent = "[ COPIED ]";
        setTimeout(() => { btn.textContent = orig; }, 1400);
      } catch { }
    });
  });

  const tabs = document.querySelectorAll(".instrument-tab");
  const panes = document.querySelectorAll(".instrument-pane");
  tabs.forEach((tab) => {
    tab.addEventListener("click", () => {
      const targetId = tab.getAttribute("data-pane");
      tabs.forEach((t) => {
        const isActive = t === tab;
        t.classList.toggle("active", isActive);
        t.setAttribute("aria-selected", isActive ? "true" : "false");
      });
      panes.forEach((p) => {
        if (p.id === targetId) {
          p.removeAttribute("hidden");
        } else {
          p.setAttribute("hidden", "");
        }
      });
    });
  });
});
