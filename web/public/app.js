/**
 * IgnisDisc site behaviour: release links, checksum drawer, copy buttons, theme toggle.
 * Edit SITE_CONFIG below (or run scripts/Publish-ReleaseAndSite.ps1) to update links.
 */
const SITE_CONFIG = {
  version: "1.08",
  githubRepoUrl: "https://github.com/tether-works/IgnisDisc",
  githubReleasesUrl: "https://github.com/tether-works/IgnisDisc/releases",
  setupDownloadUrl: "https://github.com/tether-works/IgnisDisc/releases/download/v1.08/IgnisDisc-Setup.exe",
  portableDownloadUrl: "https://github.com/tether-works/IgnisDisc/releases/download/v1.08/IgnisDisc-Portable-win-x64.zip"
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

  const checksumToggle = document.getElementById("checksumToggle");
  const checksumDrawer = document.getElementById("checksumDrawer");
  if (checksumToggle && checksumDrawer) {
    checksumToggle.addEventListener("click", () => {
      const isHidden = checksumDrawer.hasAttribute("hidden");
      if (isHidden) {
        checksumDrawer.removeAttribute("hidden");
        checksumToggle.setAttribute("aria-expanded", "true");
      } else {
        checksumDrawer.setAttribute("hidden", "");
        checksumToggle.setAttribute("aria-expanded", "false");
      }
    });
  }

  document.querySelectorAll(".btn-copy").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const text = btn.getAttribute("data-copy") || "";
      try {
        await navigator.clipboard.writeText(text);
        const orig = btn.textContent;
        btn.textContent = "Copied!";
        setTimeout(() => { btn.textContent = orig; }, 1400);
      } catch { }
    });
  });

  const themeBtn = document.getElementById("themeToggleBtn");
  const themeIcon = document.getElementById("themeIcon");
  if (themeBtn) {
    themeBtn.addEventListener("click", () => {
      const root = document.documentElement;
      const next = root.getAttribute("data-theme") === "light" ? "dark" : "light";
      root.setAttribute("data-theme", next);
      if (themeIcon) themeIcon.textContent = next === "light" ? "🌙" : "☀️";
    });
  }
});
