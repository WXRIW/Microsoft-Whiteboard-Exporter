"""Optional artifact QA. Requires pypdf and pdfplumber; not used by the application."""
import json
import sys
from pathlib import Path

import pdfplumber
from pypdf import PdfReader

folder = Path(sys.argv[1]).resolve()
results = []
for filename, expected_size in (
    ("markers.pdf", (1800, 1350)),
    ("markers-a4.pdf", (841.89, 595.28)),
    ("markers-a3.pdf", (1190.55, 841.89)),
    ("markers-pages.pdf", (841.89, 595.28)),
    ("wide-canvas.pdf", (14400, 576)),
):
    reader = PdfReader(folder / filename)
    size = tuple(float(value) for value in (reader.pages[0].mediabox.width, reader.pages[0].mediabox.height))
    assert all(abs(a - b) < 2 for a, b in zip(size, expected_size)), (filename, size)
    assert len(reader.pages) == 1 if filename != "markers-pages.pdf" else len(reader.pages) > 1
    text = "\n".join(page.extract_text() or "" for page in reader.pages)
    assert "离线重绘验收" in text, (filename, text)
    with pdfplumber.open(folder / filename) as pdf:
        for index, page in enumerate(pdf.pages):
            # Large native canvas pages remain vectors. Render QA thumbnails
            # at a bounded size instead of allocating a huge raster just to view them.
            resolution = min(110, 2400 * 72 / max(page.width, page.height))
            page.to_image(resolution=resolution).save(folder / f"{Path(filename).stem}-page-{index + 1}.png")
    results.append({"file": filename, "pages": len(reader.pages), "sizePoints": size, "chineseText": True})
print(json.dumps(results, ensure_ascii=False, indent=2))
(folder / "pdf-qa.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
