// Applies the light/dark theme chosen in ThemeService to the document root.
// The CSS in css/app.css keys all of its colour tokens off [data-theme].
window.appTheme = {
    set: function (theme) {
        document.documentElement.setAttribute('data-theme', theme);
    }
};
