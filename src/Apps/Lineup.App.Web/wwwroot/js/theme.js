// Theme management
function resolveTheme(theme) {
    if (theme === 'auto') {
        return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }
    return theme;
}

function applyTheme() {
    var theme = localStorage.getItem('app-theme') || 'auto';
    var resolved = resolveTheme(theme);
    document.documentElement.setAttribute('data-bs-theme', resolved);
    updateThemeToggleUI(theme, resolved);
}

function setTheme(theme) {
    localStorage.setItem('app-theme', theme);
    var resolved = resolveTheme(theme);
    document.documentElement.setAttribute('data-bs-theme', resolved);
    updateThemeToggleUI(theme, resolved);
    return resolved;
}

function setThemeFromUI(theme) {
    setTheme(theme);
    // Persist to server-side LineupSettings so the Settings page stays in sync
    fetch('/api/theme/' + encodeURIComponent(theme), { method: 'PUT' });
}

function getTheme() {
    return localStorage.getItem('app-theme') || 'auto';
}

function getResolvedTheme() {
    return document.documentElement.getAttribute('data-bs-theme') || 'light';
}

function updateThemeToggleUI(theme, resolved) {
    // Update toggle button icon
    var icon = document.getElementById('theme-icon');
    if (icon) {
        icon.className = resolved === 'dark' ? 'bi bi-moon-stars-fill' : 'bi bi-sun-fill';
    }
    
    // Update active state on dropdown items
    ['light', 'dark', 'auto'].forEach(function(t) {
        var item = document.getElementById('theme-opt-' + t);
        if (item) {
            if (t === theme) {
                item.classList.add('active');
            } else {
                item.classList.remove('active');
            }
        }
    });
}

// Listen for system theme changes when in auto mode
window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function(e) {
    var theme = localStorage.getItem('app-theme') || 'auto';
    if (theme === 'auto') {
        var resolved = e.matches ? 'dark' : 'light';
        document.documentElement.setAttribute('data-bs-theme', resolved);
        updateThemeToggleUI(theme, resolved);
    }
});

// Apply theme + toggle UI on initial page load
applyTheme();
