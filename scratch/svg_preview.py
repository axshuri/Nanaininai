#!/usr/bin/env python3
from pathlib import Path
import base64, urllib.request, urllib.error

RASTERIZE_URL = "https://cdn.jsdelivr.net/npm/rasterizehtml@latest/dist/rasterizeHTML.js"
OUT_WIDTH_CSS_PX = 900  # conservative GitHub content width for full-width previews

def fetch_rasterize_html():
    req = urllib.request.Request(RASTERIZE_URL, headers={"User-Agent": "svg-preview/1.0"})
    with urllib.request.urlopen(req, timeout=20) as resp:
        return resp.read()

def svg_to_png(svg_text, css_width_px):
    html = f"""<!doctype html>
<html>
<head>
<meta charset="utf-8">
<script src="data:text/javascript;base64,{base64.b64encode(fetch_rasterize_html()).decode('ascii')}"></script>
<style>
  html, body {{ margin:0; padding:0; background:#0e1116; }}
  .wrap {{ width:{css_width_px}px; margin:0 auto; }}
</style>
</head>
<body>
<div class="wrap">
{svg_text}
</div>
</body>
</html>"""
    import subprocess, sys
    try:
        r = subprocess.run(
            ["chromium", "--headless", "--disable-gpu", "--no-sandbox",
             "--screenshot=preview.png", "--window-size=%d,2000" % (css_width_px,),
             "about:blank"],
            input=html.encode("utf-8"),
            capture_output=True,
            timeout=60,
        )
    except FileNotFoundError:
        try:
            r = subprocess.run(
                ["chromium-browser", "--headless", "--disable-gpu", "--no-sandbox",
                 "--screenshot=preview.png", "--window-size=%d,2000" % (css_width_px,),
                 "about:blank"],
                input=html.encode("utf-8"),
                capture_output=True,
                timeout=60,
            )
        except FileNotFoundError:
            print("no chromium found; cannot rasterize", file=sys.stderr)
            return None
    if r.returncode != 0:
        print("chromium failed:", r.stderr.decode("utf-8", "replace"), file=sys.stderr)
        return None
    return Path("preview.png").read_bytes()

def main():
    base = Path("assets/readme")
    out_base = Path("assets/readme/preview")
    out_base.mkdir(exist_ok=True)
    for name in ["hero.svg", "header-pipeline.svg", "header-architecture.svg"]:
        src = base / name
        if not src.exists():
            print("missing", name)
            continue
        svg_text = src.read_text(encoding="utf-8")
        png = svg_to_png(svg_text, OUT_WIDTH_CSS_PX)
        if png is None:
            print("skip", name)
            continue
        out = out_base / name.replace(".svg", ".png")
        out.write_bytes(png)
        print("preview", out, len(png), "bytes")

if __name__ == "__main__":
    main()
