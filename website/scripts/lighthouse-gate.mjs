import { mkdirSync, readFileSync, writeFileSync, statSync } from "node:fs";
import { createServer } from "node:http";
import { extname, join, normalize } from "node:path";
import { gzipSync } from "node:zlib";
import lighthouse from "lighthouse";
import desktopConfig from "lighthouse/core/config/desktop-config.js";
import * as chromeLauncher from "chrome-launcher";

const PORT = 4173;
const BASE = `http://localhost:${PORT}`;
const URLS = ["/", "/examples"];
const FORM_FACTORS = ["mobile", "desktop"];
const MAX_ATTEMPTS = Number(process.env.LIGHTHOUSE_ATTEMPTS ?? 3);
const SCORED_CATEGORIES = ["performance", "accessibility", "best-practices", "seo"];
const MIN_SCORE = 0.99;
const AGENTIC_CATEGORY = "agentic-browsing";
const OUT_DIR = ".lighthouse";

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript",
  ".css": "text/css",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".ico": "image/x-icon",
  ".woff2": "font/woff2",
  ".txt": "text/plain; charset=utf-8",
  ".xml": "application/xml",
  ".webmanifest": "application/manifest+json",
};

// Resolution mirrors the production nginx: try the file, then the directory index
// (which is how the prerendered /examples/index.html is reached), then the SPA fallback.
function startServer() {
  const server = createServer((req, res) => {
    const path = normalize(new URL(req.url, BASE).pathname).replaceAll("..", "");
    const isFile = (c) => {
      try {
        return statSync(c).isFile();
      } catch {
        return false;
      }
    };
    const candidates = [join("dist", path), join("dist", path, "index.html"), "dist/index.html"];
    const file = candidates.find(isFile);
    const headers = { "content-type": MIME[extname(file)] ?? "application/octet-stream" };
    let body = readFileSync(file);
    // Compress text like the production nginx does - Lighthouse's simulation prices
    // resources by transfer size, so serving uncompressed would skew every metric.
    const compressible = /^(text\/|application\/(xml|manifest|javascript))/.test(headers["content-type"]) || file.endsWith(".svg");
    if (compressible && (req.headers["accept-encoding"] ?? "").includes("gzip")) {
      body = gzipSync(body);
      headers["content-encoding"] = "gzip";
    }
    res.writeHead(200, headers);
    res.end(body);
  });
  server.listen(PORT);
  return server;
}

function thresholdFor(category) {
  return category === AGENTIC_CATEGORY ? 1 : MIN_SCORE;
}

function scoresOf(lhr) {
  return Object.fromEntries(
    [...SCORED_CATEGORIES, AGENTIC_CATEGORY].map((c) => [c, lhr.categories[c]?.score ?? 0]),
  );
}

function passes(scores) {
  return Object.entries(scores).every(([category, score]) => score >= thresholdFor(category));
}

function failingAudits(lhr) {
  const lines = [];
  for (const category of [...SCORED_CATEGORIES, AGENTIC_CATEGORY]) {
    if ((lhr.categories[category]?.score ?? 0) >= thresholdFor(category)) continue;
    for (const ref of lhr.categories[category]?.auditRefs ?? []) {
      const audit = lhr.audits[ref.id];
      if (audit?.score !== null && audit?.score < 1) {
        lines.push(`      ${category}: ${ref.id} (${audit.title})`);
      }
    }
  }
  return lines;
}

mkdirSync(OUT_DIR, { recursive: true });
const server = startServer();
const chrome = await chromeLauncher.launch({
  chromeFlags: ["--headless=new", "--no-sandbox"],
  chromePath: process.env.CHROME_PATH,
});

const failures = [];
try {
  for (const formFactor of FORM_FACTORS) {
    for (const url of URLS) {
      // Fast path: one audit. Only a below-threshold result triggers re-measurement
      // (best score wins), so throttling variance on shared CI runners cannot flake
      // the gate without costing time on green runs.
      let best = null;
      let lastLhr = null;
      for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
        const config = formFactor === "desktop" ? desktopConfig : undefined;
        const result = await lighthouse(BASE + url, { port: chrome.port, output: "html", quiet: true }, config);
        const scores = scoresOf(result.lhr);
        lastLhr = result.lhr;
        writeFileSync(`${OUT_DIR}/${formFactor}${url.replaceAll("/", "-")}-${attempt}.report.html`, result.report);
        if (best === null || Object.values(scores).reduce((a, b) => a + b) > Object.values(best).reduce((a, b) => a + b)) {
          best = scores;
        }
        if (passes(best)) break;
      }
      const summary = Object.entries(best).map(([c, s]) => `${c}=${Math.round(s * 100)}`).join("  ");
      console.log(`${formFactor.padEnd(7)} ${url.padEnd(10)} ${summary}`);
      if (!passes(best)) {
        const bad = Object.entries(best)
          .filter(([c, s]) => s < thresholdFor(c))
          .map(([c, s]) => `${c}=${Math.round(s * 100)}`);
        failures.push(`${formFactor} ${url}: ${bad.join(", ")} (after ${MAX_ATTEMPTS} attempts)`);
        failures.push(...failingAudits(lastLhr));
      }
    }
  }
} finally {
  chrome.kill();
  server.close();
}

if (failures.length > 0) {
  console.error(`\nLighthouse gate FAILED:\n  ${failures.join("\n  ")}`);
  console.error(`HTML reports are in website/${OUT_DIR}/`);
  process.exit(1);
}
console.log(`\nLighthouse gate passed: all categories >= ${MIN_SCORE * 100} (${AGENTIC_CATEGORY} = 100) on ${FORM_FACTORS.join(" + ")}.`);
