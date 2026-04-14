/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "./Components/**/*.razor",
    "./wwwroot/**/*.html",
    "./wwwroot/**/*.js",
  ],
  theme: {
    extend: {
      colors: {
        'primary': '#005bbf',
        'primary-container': '#1a73e8',
        'secondary': '#006e2c',
        'secondary-container': '#86f898',
        'tertiary': '#b81d17',
        'surface': '#f7f9ff',
        'surface-container-low': '#f1f4fa',
        'surface-container': '#ebeef4',
        'surface-container-high': '#e5e8ee',
        'surface-container-highest': '#dfe3e8',
        'on-surface': '#181c20',
        'on-surface-variant': '#414754',
        'outline': '#727785',
        'outline-variant': '#c1c6d6',
      },
      fontFamily: {
        'headline': ['Manrope', 'sans-serif'],
        'body': ['Inter', 'sans-serif'],
      },
    }
  },
  plugins: [],
}
