import sys

with open(sys.argv[1], "r", encoding="utf-8") as f:
    c = f.read()

# Fix ?? operator type mismatch - convert both sides to string
old = "rp.GetFirstChild<D.SolidFill>()?.RgbColorModelHex?.Val ?? rp.GetFirstChild<D.SolidFill>()?.SchemeColor?.Val"
new = "rp.GetFirstChild<D.SolidFill>()?.RgbColorModelHex?.Val?.ToString() ?? rp.GetFirstChild<D.SolidFill>()?.SchemeColor?.Val?.ToString()"
if old in c:
    c = c.replace(old, new)
    print("Fixed ?? type mismatch")
else:
    print("Pattern not found")
    for i, l in enumerate(c.split(chr(10))):
        if "SolidFill" in l:
            print(f"  Line {i+1}: {l!r}")

with open(sys.argv[1], "w", encoding="utf-8") as f:
    f.write(c)
print("Done")
