document.addEventListener("DOMContentLoaded", () => {
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
