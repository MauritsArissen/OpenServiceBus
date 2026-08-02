import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

const ROUTES = [
  { url: "/", out: "dist/index.html" },
  { url: "/examples", out: "dist/examples/index.html" },
];

const { render } = await import(pathToFileURL("dist-ssr/entry-server.js"));

let template = readFileSync("dist/index.html", "utf8");

const link = template.match(/<link[^>]+href="\/(assets\/[^"]+\.css)"[^>]*>/);
if (!link) {
  throw new Error("prerender: no stylesheet link found in dist/index.html");
}
const css = readFileSync(`dist/${link[1]}`, "utf8");
template = template.replace(link[0], `<style>${css}</style>`);
console.log(`prerender: inlined ${link[1]} (${(css.length / 1024).toFixed(1)} kB)`);

for (const route of ROUTES) {
  const body = render(route.url).replace(/<link rel="preload" as="image"[^>]*\/?>/g, "");
  const html = template.replace('<div id="root"></div>', `<div id="root">${body}</div>`);
  if (html === template) {
    throw new Error("prerender: root placeholder not found in template");
  }
  mkdirSync(route.out.substring(0, route.out.lastIndexOf("/")), { recursive: true });
  writeFileSync(route.out, html);
  console.log(`prerender: ${route.url} -> ${route.out} (${(html.length / 1024).toFixed(1)} kB)`);
}
