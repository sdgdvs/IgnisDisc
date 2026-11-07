const SNIPPETS = {
  protocol: {
    badge: "AGENT HARNESS / RULES",
    code: `# Anti-Vibe-Coding Protocol (Enforced Across All Workspaces)
1. Zero Sycophancy: Never declare a task "Done" because files were edited.
2. Autonomous Verifiability: Every feature must be proven by a deterministic
   simulator, automated test suite, or local VLM screenshot check.
3. Forward-Fixability: Strict modular boundaries so agents can fix issues
   forward without breaking unrelated systems.
4. Zero Warnings Gate: Build + full test suite must exit 0 before completion.`,
    footer: "// Keeps autonomous runs honest without human code review"
  },
  simulator: {
    badge: "IGNISDISC / 24-PHASE PIPELINE",
    code: `Phase 01..04 -> Build VirtualDiscBackend (in-memory optical drive simulator)
Phase 05..18 -> Implement ISO/UDF, El Torito, Audio DSP, Ripper & PAR2 Archiver
Phase 19..24 -> Wire WPF Desktop Studio, Headless CLI & Single-File Setup.exe
Verification -> 481 automated tests pass in ~6s + 4-pass physical CD-RW suite`,
    footer: "// 481 automated tests run in CI with zero physical hardware required"
  },
  vlm: {
    badge: "LOCAL VLM / VISUAL QA",
    code: `{
  "task": "vision_confirm_pair",
  "frames": ["capture_t0.jpg", "capture_t1_confirm.jpg"],
  "fps_budget_met": true,
  "persistent_visual_issues": [],
  "verdict": "PASS"
}`,
    footer: "// Automated visual inspection catches UI and 3D rendering bugs unattended"
  }
};

function renderSnippet(key) {
  const item = SNIPPETS[key] || SNIPPETS.protocol;
  const badgeEl = document.getElementById("consoleBadge");
  const codeEl = document.getElementById("consoleCode");
  const footerEl = document.getElementById("consoleFooter");

  if (badgeEl) badgeEl.textContent = item.badge;
  if (codeEl) codeEl.textContent = item.code;
  if (footerEl) footerEl.textContent = item.footer;
}

document.addEventListener("DOMContentLoaded", () => {
  renderSnippet("protocol");

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
