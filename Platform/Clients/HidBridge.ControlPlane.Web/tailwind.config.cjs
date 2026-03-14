/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "./Components/**/*.{razor,html,cshtml}",
    "./wwwroot/**/*.html",
    "./Identity/**/*.cs",
    "./Services/**/*.cs",
    "./Models/**/*.cs",
    "./Localization/**/*.cs",
    "./Program.cs"
  ],
  theme: {
    extend: {}
  },
  plugins: []
};
