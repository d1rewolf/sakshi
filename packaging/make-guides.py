#!/usr/bin/env python3
"""Builds one PDF guide per OS from docs/install.md and docs/user-guide.md.

Each PDF holds the install guide with only that OS's install section (Omarchy
notes on Linux only), followed by the user guide, so a new user gets two
files: the installer and its guide.

Needs pandoc; WeasyPrint makes the PDF.
Usage: uv run --with weasyprint==70.0 packaging/make-guides.py OUTPUT_DIR
"""
import pathlib, re, subprocess, sys, tempfile

import weasyprint

ROOT = pathlib.Path(__file__).resolve().parent.parent
OSES = {"Windows": "Windows", "macOS": "macOS", "Linux": "Linux"}
OS_SECTIONS = {"Windows", "macOS", "Linux", "Omarchy"}

CSS = """
@page { size: Letter; margin: 18mm 16mm; @bottom-center { content: counter(page); font-size: 8pt; color: #888; } }
body { font-family: "Inter", "Segoe UI", "Helvetica Neue", Arial, sans-serif; font-size: 10.5pt;
       line-height: 1.45; color: #1d1d1f; max-width: 46em; margin: 0 auto; }
h1 { font-size: 20pt; margin-top: 0; border-bottom: 2px solid #ddd; padding-bottom: .2em; }
h1.part { page-break-before: always; }
h2 { font-size: 14pt; margin-top: 1.4em; }
h3 { font-size: 11.5pt; }
code { font-family: "JetBrains Mono", "Menlo", "Consolas", monospace; font-size: 9pt;
       background: #f2f2f4; padding: 0 .2em; border-radius: 3px; }
pre { background: #f2f2f4; padding: .6em .8em; border-radius: 4px; white-space: pre-wrap; }
pre code { background: none; padding: 0; }
table { border-collapse: collapse; margin: .6em 0; width: 100%; }
th, td { border: 1px solid #d0d0d5; padding: .3em .5em; vertical-align: top; text-align: left; }
th { background: #f2f2f4; }
a { color: #0b57d0; text-decoration: none; }
.title { text-align: center; margin-bottom: 2em; }
.title p { color: #555; }
"""

def sections(md):
    """Splits a document at its level-2 headings: [(heading or None, text)]."""
    parts, current, heading = [], [], None
    for line in md.splitlines(keepends=True):
        m = re.match(r"## (.+)", line)
        if m:
            parts.append((heading, "".join(current)))
            heading, current = m.group(1).strip(), []
        current.append(line)
    parts.append((heading, "".join(current)))
    return parts

def install_for(os_name, md):
    keep = []
    for heading, text in sections(md):
        if heading in OS_SECTIONS and not (heading == os_name or (heading == "Omarchy" and os_name == "Linux")):
            continue
        keep.append(text)
    return "".join(keep)

def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    out = pathlib.Path(sys.argv[1]).expanduser().resolve()
    out.mkdir(parents=True, exist_ok=True)
    install = (ROOT / "docs" / "install.md").read_text()
    guide = (ROOT / "docs" / "user-guide.md").read_text()
    # One document: links between the two files become links within it.
    guide = guide.replace("](install.md#", "](#").replace("](install.md)", "](#installing-sakshi)")
    with tempfile.TemporaryDirectory() as tmp:
        tmp = pathlib.Path(tmp)
        (tmp / "style.css").write_text(CSS)
        for os_name in OSES:
            body = install_for(os_name, install).replace("](user-guide.md)", "](#using-sakshi)")
            md = body + "\n\n" + guide.replace("# Using Sakshi", '# Using Sakshi {.part}', 1)
            src = tmp / f"{os_name}.md"
            src.write_text(md)
            html = tmp / f"{os_name}.html"
            subprocess.run(["pandoc", str(src), "-f", "gfm+attributes", "-s", "-o", str(html), "--css", "style.css",
                            "--embed-resources", "--metadata", f"title=Sakshi for {os_name}"], check=True, cwd=tmp)
            pdf = out / f"Sakshi-guide-{os_name}.pdf"
            weasyprint.HTML(filename=str(html)).write_pdf(str(pdf))
            print(pdf)

if __name__ == "__main__":
    main()
