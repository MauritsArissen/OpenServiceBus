// Single writer for the browser-chrome colour (the `theme-color` meta): iOS Safari
// tints the URL pill and overscroll areas with it. Two states feed into it - the app
// theme and whether a dark scrim currently covers the page (the mobile drawer). Safari
// blends visible scrims into its chrome tint on its own but is known to leave the
// dimmed tint stuck after the overlay unmounts; owning the value on both open AND
// close forces it back every time. Colours mirror --background in index.css, dimmed
// variants are the background blended with the scrim (black at 50%).

let dimmed = false;

export function syncThemeColor() {
  const meta = document.querySelector('meta[name="theme-color"]');
  if (!meta) return;
  const dark = document.documentElement.classList.contains("dark");
  const color = dimmed ? (dark ? "#050505" : "#808080") : dark ? "#0a0a0a" : "#ffffff";
  meta.setAttribute("content", color);
}

export function setThemeColorDimmed(value: boolean) {
  dimmed = value;
  syncThemeColor();
}
