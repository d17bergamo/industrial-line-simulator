#!/usr/bin/env python3
"""Prepara la versione web per la pubblicazione come pagina.

La cartella "_framework" di Blazor non si può pubblicare (i nomi che iniziano con "_" sono riservati): la rinomina in "fw"
e aggiorna i riferimenti. I controlli di integrità dei file toccati vengono tolti da index.html, perché il contenuto cambia.
Uso: web-pack.py <cartella wwwroot>
"""
import json, os, re, sys

root = sys.argv[1]
os.rename(os.path.join(root, "_framework"), os.path.join(root, "fw"))

for name in os.listdir(os.path.join(root, "fw")):
    if name.startswith("blazor.webassembly") and name.endswith(".js"):
        path = os.path.join(root, "fw", name)
        text = open(path, encoding="utf-8").read()
        open(path, "w", encoding="utf-8").write(text.replace("_framework", "fw"))

index = os.path.join(root, "index.html")
html = open(index, encoding="utf-8").read().replace("_framework", "fw")
html = re.sub(r'\s*<link[^>]*rel="preload"[^>]*>', "", html)

def clean(match):
    data = json.loads(match.group(1))
    data.pop("integrity", None)
    return '<script type="importmap">' + json.dumps(data, indent=2) + "</script>"

html = re.sub(r'<script type="importmap">(.*?)</script>', clean, html, flags=re.S)
open(index, "w", encoding="utf-8").write(html)
left = [n for n in os.listdir(os.path.join(root, "fw")) if not n.endswith((".js", ".wasm"))]
print("cartella fw pronta;", "file con estensione non pubblicabile: " + ", ".join(left) if left else "tutti i file sono .js o .wasm")
