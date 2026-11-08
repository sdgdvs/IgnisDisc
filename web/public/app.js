/**
 * IgnisDisc Optical Studio Configuration
 * Edit SITE_CONFIG below (or run scripts/Publish-ReleaseAndSite.ps1) to update links.
 */
const SITE_CONFIG = {
  version: "1.08",
  githubRepoUrl: "https://github.com/tether-works/IgnisDisc",
  setupDownloadUrl: "https://github.com/tether-works/IgnisDisc/releases/download/v1.08/IgnisDisc-Setup.exe",
  portableDownloadUrl: "https://github.com/tether-works/IgnisDisc/releases/download/v1.08/IgnisDisc-Portable-win-x64.zip",
  engineer: {
    initials: "TW",
    name: "tetherworks",
    role: "Open-Source Hardware & Software Systems",
    email: "mailto:developer@tetherworks.local?subject=IgnisDisc%20%2F%20Systems%20Engineering%20Note"
  }
};

const STUDIO_VIEWS = {
  audio: {
    title: "Red Book Audio CD Studio (DAO / CD-Text / C2 Secure Rip)",
    badge: "PCM 44.1 kHz • 16-Bit Stereo • 2352B/Sector",
    headers: ["Trk", "Track / Source File", "Duration", "ISRC / AccurateRip", "Mode / Status"],
    rows: [
      ["01", "01-Subchannel-Prelude.flac", "03:42.18", "ARv2: Verified (Confidence 42)", "Media Foundation PCM"],
      ["02", "02-SPTD-PassThrough.wav", "04:15.60", "ARv2: Verified (Confidence 42)", "Gapless (00:00 Pre-Gap)"],
      ["03", "03-LeadIn-CRC16.flac", "05:08.04", "C2 Pointer: 1 Sector Recovered", "MajorityVote Retry OK"],
      ["04", "04-Spindle-SpinDown-0x1B.wav", "03:54.52", "CD-Text Pack 0x80..0x8F Ready", "DAO Lead-In Queued"]
    ],
    footerLeft: "Disc Capacity: 17:00 / 79:59 • Read Offset: +6 Samples • C2 Pointers: Enabled",
    footerRight: "✓ Bit-Perfect DAO / Secure Rip"
  },
  data: {
    title: "Multi-Filesystem Data & Multi-Session Mastering",
    badge: "ISO 9660 + Joliet UCS-2BE + UDF 1.02 Bridge",
    headers: ["Path", "Size", "Filesystem Bridge", "Start LBA", "Session"],
    rows: [
      ["/bin/IgnisDisc.exe", "68.4 MB", "UDF 1.02 + Joliet", "LBA 0000288", "Session 1 (Imported)"],
      ["/bin/ignisdisc-cli.exe", "14.2 MB", "UDF 1.02 + Joliet", "LBA 0035310", "Session 1 (Imported)"],
      ["/backups/2026-11-delta.tar.zst", "112.0 MB", "UDF 1.02 + Joliet", "LBA 0045120", "Session 2 (Appending)"],
      ["/BOOT/ETFSBOOT.COM + EFISYS.BIN", "2.88 MB", "El Torito BIOS + UEFI", "LBA 0000032", "Boot Catalog Valid"]
    ],
    footerLeft: "Write Speed: Max Supported (Negotiated) • AVDP @ LBA 256 & N-1",
    footerRight: "✓ Multi-Session + Post-Burn SHA-256 Verify"
  },
  archive: {
    title: "Multi-Disc Archive Studio (Spanning + SHA-256 Manifest + PAR2)",
    badge: "ARCHIVE_MANIFEST.json • par2cmdline-turbo",
    headers: ["Disc", "Volume Label", "Files", "Payload + Parity", "Verification"],
    rows: [
      ["Disc 1 of 3", "PHOTOS_2026_D01", "412 files", "4.12 GB + 10% PAR2 (412 MB)", "SHA-256 Manifest + Parity OK"],
      ["Disc 2 of 3", "PHOTOS_2026_D02", "389 files", "4.15 GB + 10% PAR2 (415 MB)", "SHA-256 Manifest + Parity OK"],
      ["Disc 3 of 3", "PHOTOS_2026_D03", "194 files", "2.08 GB + 10% PAR2 (208 MB)", "Ready to Stage & Burn"]
    ],
    footerLeft: "Set ID: 5e54d2b8b530 • Self-Contained Manifest Written to Every Disc",
    footerRight: "✓ Survives Scratched Sectors via PAR2 Repair"
  },
  dvd: {
    title: "DVD-Video Mastering (32 KB ECC Sector Alignment)",
    badge: "UDF 1.02 + ISO 9660 • 16-Sector IFO/BUP Separation",
    headers: ["Structure", "Extent LBA", "Alignment", "Size", "Compliance"],
    rows: [
      ["/VIDEO_TS/VIDEO_TS.IFO", "LBA 0000300", "32 KB ECC Boundary", "12.0 KB", "IFO/BUP Separated >= 16 Sectors"],
      ["/VIDEO_TS/VIDEO_TS.VOB", "LBA 0000316", "Contiguous Extent", "38.0 KB", "Hardware Player Compliant"],
      ["/VIDEO_TS/VIDEO_TS.BUP", "LBA 0000336", "32 KB ECC Boundary", "12.0 KB", "Backup Header Verified"],
      ["/VIDEO_TS/VTS_01_1.VOB", "LBA 0000400", "Contiguous Extent", "1,024.0 MB", "Book-Type: DVD-ROM Bitset"]
    ],
    footerLeft: "Target: Standalone Set-Top DVD Players • Write Mode: DAO Incremental",
    footerRight: "✓ Strict DVD-Video Spec Verified"
  },
  scan: {
    title: "Optical Surface Scan & Rewritable Media Utilities",
    badge: "SCSI Format 0x02 Full TOC • Read10 / ReadCD",
    headers: ["LBA Range", "Track / Session", "Read Speed", "Block Status", "Sense / C2"],
    rows: [
      ["000000 .. 001023", "Session 1 (Data)", "10.0x CAV", "100% Good Blocks", "0x00 / 0x00 / 0x00 (OK)"],
      ["001024 .. 002077", "Session 1 (Data)", "10.0x CAV", "100% Good Blocks", "TAO Run-Out Trimmed"],
      ["011550 .. 011580", "Session 2 (Appended)", "10.0x CAV", "100% Good Blocks", "0 Damaged Blocks"]
    ],
    footerLeft: "Drive: Optical Drive (DVD/CD-RW) • Post-Op: SCSI 0x1B Spindle Spin-Down",
    footerRight: "✓ 0 Damaged Sectors"
  }
};

function renderStudioView(viewKey) {
  const view = STUDIO_VIEWS[viewKey] || STUDIO_VIEWS.audio;
  const titleEl = document.getElementById("studioTitle");
  const badgeEl = document.getElementById("studioBadge");
  const theadEl = document.getElementById("studioThead");
  const tbodyEl = document.getElementById("studioTbody");
  const footerLeftEl = document.getElementById("studioFooterLeft");
  const footerRightEl = document.getElementById("studioFooterRight");

  if (titleEl) titleEl.textContent = view.title;
  if (badgeEl) badgeEl.textContent = view.badge;
  if (footerLeftEl) footerLeftEl.textContent = view.footerLeft;
  if (footerRightEl) footerRightEl.textContent = view.footerRight;

  if (theadEl) {
    theadEl.innerHTML = "<tr>" + view.headers.map((h) => `<th>${h}</th>`).join("") + "</tr>";
  }
  if (tbodyEl) {
    tbodyEl.innerHTML = view.rows
      .map((row) => "<tr>" + row.map((cell) => `<td>${cell}</td>`).join("") + "</tr>")
      .join("");
  }
}

document.addEventListener("DOMContentLoaded", () => {
  const setHref = (id, url) => {
    const el = document.getElementById(id);
    if (el && url) el.setAttribute("href", url);
  };

  setHref("navGithubLink", SITE_CONFIG.githubRepoUrl);
  setHref("ctaGithub", SITE_CONFIG.githubRepoUrl);
  setHref("btnDownloadSetup", SITE_CONFIG.setupDownloadUrl);
  setHref("btnDownloadPortable", SITE_CONFIG.portableDownloadUrl);

  const verEl = document.getElementById("navVersion");
  if (verEl && SITE_CONFIG.version) verEl.textContent = `v${SITE_CONFIG.version}`;

  renderStudioView("audio");

  document.querySelectorAll(".studio-tab").forEach((tab) => {
    tab.addEventListener("click", () => {
      document.querySelectorAll(".studio-tab").forEach((t) => t.classList.remove("active"));
      tab.classList.add("active");
      renderStudioView(tab.getAttribute("data-view"));
    });
  });

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
