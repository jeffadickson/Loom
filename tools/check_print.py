"""Check the synthetic --bands PDF fixture; requires PyMuPDF."""
import re
import sys
import fitz

document = fitz.open(sys.argv[1])
assert len(document) > 1
texts = []
for number, page in enumerate(document, 1):
    text = page.get_text()
    texts.append(text)
    for expected in (
        "LABORATORY EQUIPMENT - CONTROLLED REGISTER",
        "Engineering copy",
        f"Page {number} of {len(document)}",
    ):
        boxes = page.search_for(expected)
        assert len(boxes) == 1, (number, expected)
        assert page.rect.contains(boxes[0]), (number, expected, boxes[0])
    assert page.search_for("LABORATORY EQUIPMENT")[0].y1 < 50
    assert page.search_for("Engineering copy")[0].y0 > page.rect.height - 50
all_text = "\n".join(texts)
assert all_text.count("Calibration notes") == 1
assert all_text.count("Approved by:") == 1
for number in range(95):
    assert len(re.findall(rf"Instrument {number:03d}\b", all_text)) == 1, number
print(f"PASS PDF: {len(document)} pages, repeated bands/numbers inside page bounds, all instruments and notes exactly once")
