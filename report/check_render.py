"""Render check for a generated SparkVS report: headless Chrome DOM dump (JS executed) + screenshot pixel stats.
Usage: python -I check_render.py <report.html>"""
import os, re, subprocess, sys

CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
src = os.path.abspath(sys.argv[1])
base = os.path.splitext(src)[0]
url = "file:///" + src.replace("\\", "/")
prof = os.path.join(os.environ["TEMP"], "sparkvs-chrome-prof")
common = [CHROME, "--headless=new", "--disable-gpu", "--no-first-run", "--user-data-dir=" + prof, "--window-size=1280,2600", "--hide-scrollbars"]
dom = subprocess.run(common + ["--virtual-time-budget=3000", "--dump-dom", url], capture_output=True, text=True, encoding="utf-8").stdout
open(base + ".dom.html", "w", encoding="utf-8").write(dom)
subprocess.run(common + ["--screenshot=" + base + ".png", url], capture_output=True)
for k, v in re.findall(r'<div class="card[^"]*"><div class="k">(.*?)</div><div class="v">(.*?)</div>', dom):
    print("CARD", k, "=", v)
print("dom bytes", len(dom), "| svg", dom.count("<svg"), "| polylines", dom.count("<polyline"), "| rects", dom.count("<rect"),
      "| tree rows rendered", dom.count('class="node"'), "| nodata blocks", dom.count('class="nodata"'),
      "| sections", len(re.findall(r'<section id="(\w+)"', dom)))
from PIL import Image
im = Image.open(base + ".png").convert("RGB")
print("png", im.size, os.path.getsize(base + ".png"), "bytes | distinct colors (downsampled)", len(set(im.resize((320, 650)).getdata())))
