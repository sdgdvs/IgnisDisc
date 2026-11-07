/**
 * IgnisDisc Optical Studio + FDE Case Study Configuration
 * Edit SITE_CONFIG below (or run scripts/Publish-ReleaseAndSite.ps1) to update links.
 */
const SITE_CONFIG = {
  version: "1.08",
  githubRepoUrl: "https://github.com/tether-works/IgnisDisc",
  setupDownloadUrl: "https://github.com/tether-works/IgnisDisc/releases/download/v1.08/IgnisDisc-Setup.exe",
  portableDownloadUrl: "https://github.com/tether-works/IgnisDisc/releases/download/v1.08/IgnisDisc-Portable-win-x64.zip",
  engineer: {
    initials: "DT",
    name: "tetherworks Systems",
    role: "Open-Source Hardware & Software Systems",
    email: "mailto:dev@tetherworks.dev?subject=IgnisDisc%20%2F%20Systems%20Engineering%20Note"
  }
};

const STUDIO_VIEWS = {
  audio: {
    title: "Red Book Audio CD Studio (DAO / CD-Text)",
    badge: "PCM 44.1 kHz • 16-Bit Stereo • 2352B/Sector",
    rows: [
      ["01", "01-Subchannel-Prelude.flac", "03:42.18", "ISRC: US-DT6-26-00001", "Normalized (-0.1 dBFS)"],
      ["02", "02-SPTD-PassThrough.wav", "04:15.60", "ISRC: US-DT6-26-00002", "Gapless (00:00 Pre-Gap)"],
      ["03", "03-LeadIn-CRC16.m4a", "05:08.04", "ISRC: US-DT6-26-00003", "Media Foundation PCM"],
      ["04", "04-Spindle-SpinDown-0x1B.wav", "03:54.52", "ISRC: US-DT6-26-00004", "CD-Text Pack Ready"]
    ],
    headers: ["Trk", "Source File", "Duration (MM:SS.FF)", "ISRC Metadata", "DSP / Subchannel"],
    footerLeft: "Disc Capacity: 17:00 / 79:59 (152.4 MB PCM) • Write Speed: 16x Safe Floor",
    footerRight: "Burn Audio CD"
  },
  data: {
    title: "Multi-Filesystem Data & UDF Mastering",
    badge: "ISO 9660 + Joliet UCS-2BE + UDF 1.02 Bridge",
    rows: [
      ["/bin/IgnisDisc.exe", "68.4 MB", "UDF 1.02 + Joliet", "LBA 0000288", "SHA-256 Verified"],
      ["/bin/ignisdisc-cli.exe", "14.2 MB", "UDF 1.02 + Joliet", "LBA 0035310", "SHA-256 Verified"],
      ["/docs/IGNISDISC-MANIFEST.sha256", "4.8 KB", "UDF 1.02 + Joliet", "LBA 0042580", "AVDP @ LBA 256"],
      ["/VIDEO_TS/VTS_01_0.IFO", "64.0 KB", "32 KB ECC Aligned", "LBA 0045056", "16-Sector Boundary"]
    ],
    headers: ["Virtual Path", "Size", "Bridge Mode", "Physical LBA", "Alignment / Integrity"],
    footerLeft: "Volume Label: IGNIS_ARCHIVE_2026 • 2,048 Bytes/Sector • Post-Burn Verify: ON",
    footerRight: "Burn Data Disc"
  },
  boot: {
    title: "El Torito Dual BIOS + UEFI Bootable Image Studio",
    badge: "Hybrid x86 Boot Sector + EFI System Partition",
    rows: [
      ["Validation Entry", "Header ID 0x01", "x86 / x64", "Checksum 0xAA55", "El Torito Rev 1.0"],
      ["Initial/Default Entry", "etfsboot.com", "BIOS (0x00)", "4 Sectors (No Emul)", "Load Segment 0x07C0"],
      ["Section Header (EFI)", "Platform ID 0xEF", "UEFI x64", "Final Section", "Boot Indicator 0x88"],
      ["Section Entry (ESP)", "efisys_noprompt.bin", "FAT12/16 ESP", "2,880 Sectors", "UEFI Secure Boot Ready"]
    ],
    headers: ["Catalog Record", "Boot Image", "Architecture", "Sector Count", "Status"],
    footerLeft: "Target: Bootable ISO + Direct Physical Burn ([D:] HL-DT-ST) • Speed: 16x",
    footerRight: "Master Boot Disc"
  },
  scan: {
    title: "SCSI MMC-5 Surface Scanner & Drive Utilities",
    badge: "IOCTL_SCSI_PASS_THROUGH_DIRECT • CDB 0x28 / 0x1B",
    rows: [
      ["LBA 000000 – 089999", "180.0 MB", "24.6 MB/s (16.4x)", "1.8 ms avg", "100% Good (0 Retries)"],
      ["LBA 090000 – 179999", "180.0 MB", "28.1 MB/s (18.7x)", "1.6 ms avg", "100% Good (0 Retries)"],
      ["LBA 180000 – 269999", "180.0 MB", "31.4 MB/s (20.9x)", "1.5 ms avg", "100% Good (0 Retries)"],
      ["Spindle Power State", "CDB 0x1B", "START_STOP_UNIT", "LoEj=0, Start=0", "Auto Spin-Down Armed"]
    ],
    headers: ["Sector Range", "Data Read", "Transfer Rate", "Read Latency", "Surface Health"],
    footerLeft: "Multi-Session Full TOC (0x02) • Rewritable Quick/Full Erase: Ready",
    footerRight: "Run Surface Scan"
  }
};

function renderStudioTab(tabKey) {
  const view = STUDIO_VIEWS[tabKey] || STUDIO_VIEWS.audio;
  const stage = document.getElementById("studioStage");
  if (!stage) return;

  const headerCells = view.headers.map((h) => `<th>${h}</th>`).join("");
  const bodyRows = view.rows
    .map((r) => `<tr>${r.map((cell) => `<td>${cell}</td>`).join("")}</tr>`)
    .join("");

  stage.innerHTML = `
    <div>
      <div class="stage-header">
        <span class="stage-title">${view.title}</span>
        <span class="stage-badge">${view.badge}</span>
      </div>
      <table class="stage-table">
        <thead><tr>${headerCells}</tr></thead>
        <tbody>${bodyRows}</tbody>
      </table>
    </div>
    <div class="stage-footer">
      <span>${view.footerLeft}</span>
      <span class="burn-pill">${view.footerRight}</span>
    </div>
  `;
}

document.addEventListener("DOMContentLoaded", () => {
  // 1. Hydrate Links from SITE_CONFIG
  const setupBtn = document.getElementById("btnDownloadSetup");
  const portableBtn = document.getElementById("btnDownloadPortable");
  const navGithub = document.getElementById("navGithubLink");
  const ctaGithub = document.getElementById("ctaGithub");
  const ctaContact = document.getElementById("ctaContactPrimary");
  const engInitials = document.getElementById("engInitials");
  const engName = document.getElementById("engName");
  const engRole = document.getElementById("engRole");

  if (setupBtn) setupBtn.href = SITE_CONFIG.setupDownloadUrl;
  if (portableBtn) portableBtn.href = SITE_CONFIG.portableDownloadUrl;
  if (navGithub) navGithub.href = SITE_CONFIG.githubRepoUrl;
  if (ctaGithub) ctaGithub.href = SITE_CONFIG.githubRepoUrl;
  if (ctaContact) {
    // Hide the contact button until a real address is configured (placeholder .local addresses don't work).
    const email = SITE_CONFIG.engineer.email || "";
    if (!email || /@[^?]*\.local(\?|$)/i.test(email)) {
      ctaContact.style.display = "none";
    } else {
      ctaContact.href = email;
    }
  }
  if (engInitials) engInitials.textContent = SITE_CONFIG.engineer.initials;
  if (engName) engName.textContent = SITE_CONFIG.engineer.name;
  if (engRole) engRole.textContent = SITE_CONFIG.engineer.role;

  // 2. Initialize Interactive Studio Preview
  renderStudioTab("audio");
  document.querySelectorAll(".studio-tab").forEach((btn) => {
    btn.addEventListener("click", () => {
      document.querySelectorAll(".studio-tab").forEach((b) => {
        b.classList.remove("active");
        b.setAttribute("aria-selected", "false");
      });
      btn.classList.add("active");
      btn.setAttribute("aria-selected", "true");
      renderStudioTab(btn.getAttribute("data-tab"));
    });
  });

  // 3. SHA-256 Checksum Drawer & Copy Buttons
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
      const text = btn.getAttribute("data-copy");
      if (!text) return;
      try {
        await navigator.clipboard.writeText(text);
        const prev = btn.textContent;
        btn.textContent = "Copied!";
        setTimeout(() => {
          btn.textContent = prev;
        }, 1500);
      } catch {
        // Fallback no-op
      }
    });
  });

  // 4. Live Dark / Light Theme Toggle
  const themeBtn = document.getElementById("themeToggleBtn");
  const themeIcon = document.getElementById("themeIcon");
  if (themeBtn && themeIcon) {
    themeBtn.addEventListener("click", () => {
      const html = document.documentElement;
      const next = html.getAttribute("data-theme") === "light" ? "dark" : "light";
      html.setAttribute("data-theme", next);
      themeIcon.textContent = next === "dark" ? "☀️" : "🌙";
    });
  }
});
