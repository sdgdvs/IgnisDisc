const SNIPPETS = {
  ignis: {
    badge: "IGNISDISC v1.08 / VERIFICATION",
    code: `Virtual Suite : 510 / 510 automated tests passing (~4s + 25 drive/media personas)
Compiler Gate : .NET 10 Release (win-x64) — 0 Warnings, 0 Errors
Physical Media: Verified across CD-R, CD-RW, DVD-R, and DVD+RW hardware
Capabilities  : Multi-session ISO/UDF, Dual BIOS/UEFI boot, C2/AccurateRip, PAR2`,
    footer: "// Verified against both in-memory simulation and physical optical media"
  },
  kilo: {
    badge: "KILOOS v0.4.0 / FLEET SUMMARY",
    code: `Application Fleet : 95 self-contained web apps, system tools, and games
Target Frame Rate : Locked 60fps across arcade, strategy, and desktop shell
Security Pipeline : 4-layer security lint, immutability gate & provenance check
Included Modules  : KChrono, KStarForge, KRogue, KCosmic, KTerm, KNet, KBBS`,
    footer: "// Live in browser at kilo.tetherworks.dev"
  },
  cli: {
    badge: "POWERSHELL / HEADLESS CLI",
    code: `# Stage a multi-disc archive with 10% PAR2 recovery & verify the burned disc
PS C:\\> ignisdisc-cli.exe archive-stage --input "C:\\Photos" --staging "C:\\Stage" --media dvd --redundancy 10
PS C:\\> ignisdisc-cli.exe archive-verify --path D:\\ --json

# Secure-rip an audio CD to FLAC with C2 error pointers & AccurateRip verification
PS C:\\> ignisdisc-cli.exe rip-audio --drive D: --output "C:\\Music" --format flac --secure --verify-accuraterip`,
    footer: "// Full GUI + headless CLI included in both Setup.exe and Portable.zip"
  }
};

function renderSnippet(key) {
  const item = SNIPPETS[key] || SNIPPETS.ignis;
  const badgeEl = document.getElementById("consoleBadge");
  const codeEl = document.getElementById("consoleCode");
  const footerEl = document.getElementById("consoleFooter");

  if (badgeEl) badgeEl.textContent = item.badge;
  if (codeEl) codeEl.textContent = item.code;
  if (footerEl) footerEl.textContent = item.footer;
}

document.addEventListener("DOMContentLoaded", () => {
  renderSnippet("ignis");

  document.querySelectorAll(".console-tab").forEach((btn) => {
    btn.addEventListener("click", () => {
      document.querySelectorAll(".console-tab").forEach((b) => b.classList.remove("active"));
      btn.classList.add("active");
      renderSnippet(btn.getAttribute("data-tab"));
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
