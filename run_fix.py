
import sys, re

with open(sys.argv[1], "r", encoding="utf-8") as f:
    c = f.read()

# 1. Remove using DocumentFormat.OpenXml.Drawing;
c = chr(10).join(l for l in c.split(chr(10)) if l.strip() != "using DocumentFormat.OpenXml.Drawing;")
print("1 OK: Removed Drawing using")

# 2. Add using System.Text.RegularExpressions; after using System.Text;
lines = c.split(chr(10))
nl = []
for l in lines:
    nl.append(l)
    if l.strip() == "using System.Text;":
        nl.append("using System.Text.RegularExpressions;")
c = chr(10).join(nl)
print("2 OK: Added Regex using")

# 3. Fix ExcelRead bad block
old = "var sheetPart = wbPart!.WorksheetParts.First(sp =>" + chr(10) + "                wbPart.GetPartById(sp.GetParentRelationships().First().Id)?.Equals(sp) ?? false);" + chr(10) + "" + chr(10) + "            var data = wbPart.Workbook.Descendants<Sheet>()"
new = "var data = wbPart!.Workbook.Descendants<Sheet>()"
if old in c:
    c = c.replace(old, new)
    print("3 OK: Fixed ExcelRead")
else:
    print("3 WARN: ExcelRead block not found - might already be fixed")

# 4. Fix GetOrCreateSheets
old = "var sheets = tgtWb.Workbook.GetOrCreateSheets();"
new = "var sheets = tgtWb.Workbook.Sheets ?? tgtWb.Workbook.AppendChild(new Sheets());"
if old in c:
    c = c.replace(old, new)
    print("4 OK: Fixed GetOrCreateSheets")
else:
    print("4 WARN: GetOrCreateSheets not found")

# 5. Fix char[] vs string with ??
old = "new string(cell.CellReference?.ToString()?.TakeWhile(char.IsLetter).ToArray() ?? " + chr(34) + "A" + chr(34) + ")"
new = "new string(cell.CellReference?.ToString()?.TakeWhile(char.IsLetter).ToArray() ?? [])"
if old in c:
    c = c.replace(old, new)
    print("5 OK: Fixed char[] vs string")
else:
    print("5 WARN: char[] pattern not found")

# 6. Rename loop variable cell -> srcCell in ExcelCopyRange
old = "foreach (var cell in srcRow.Elements<Cell>())"
new = "foreach (var srcCell in srcRow.Elements<Cell>())"
if old in c:
    c = c.replace(old, new, 1)
    c = c.replace("cell.DataType?.Value == CellValues.SharedString && cell.CellValue?.Text", "srcCell.DataType?.Value == CellValues.SharedString && srcCell.CellValue?.Text")
    c = c.replace("int.Parse(cell.CellValue.Text)", "int.Parse(srcCell.CellValue.Text)")
    c = c.replace("cell.CellValue?.CloneNode(true) as CellValue", "srcCell.CellValue?.CloneNode(true) as CellValue")
    c = c.replace("cell.DataType?.CloneNode(true) as EnumValue<CellValues>", "srcCell.DataType?.Value")
    print("6 OK: Renamed loop variable")
else:
    print("6 WARN: loop variable not found")

# 7. Fix StyleDefinitions -> Styles
old = "tgtStyles.StyleDefinitions?.Save()"
new = "tgtStyles.Styles?.Save()"
if old in c:
    c = c.replace(old, new)
    print("7 OK: Fixed StyleDefinitions")
else:
    print("7 WARN: StyleDefinitions not found")

# 8. Fix P.SlideLayoutPart -> SlideLayoutPart
count = c.count("P.SlideLayoutPart")
c = c.replace("P.SlideLayoutPart", "SlideLayoutPart")
print("8 OK: Fixed P.SlideLayoutPart -> SlideLayoutPart (count: " + str(count) + ")")

# 9. Fix P.SlidePart -> SlidePart (in AddSlide method)
count = c.count("P.SlidePart")
c = c.replace("P.SlidePart", "SlidePart")
print("9 OK: Fixed P.SlidePart -> SlidePart (count: " + str(count) + ")")

# 10. Fix decimal y -> long y
old = "var y = 10m;"
new = "long y = 10;"
if old in c:
    c = c.replace(old, new)
    print("10 OK: Fixed decimal y -> long y")
else:
    print("10 WARN: decimal y not found")

# 11. Fix WordWrite types (Paragraph, Run, Text -> Wordprocessing.*)
# In WordWrite method only
c = c.replace("new Paragraph(new Run(new Text(t[2..])", "new Wordprocessing.Paragraph(new Wordprocessing.Run(new Wordprocessing.Text(t[2..])")
c = c.replace("new Paragraph(new Run(new Text(line)))", "new Wordprocessing.Paragraph(new Wordprocessing.Run(new Wordprocessing.Text(line)))")
c = c.replace("new Paragraph(new Run(new Text(line)));", "new Wordprocessing.Paragraph(new Wordprocessing.Run(new Wordprocessing.Text(line)));")
print("11 OK: Fixed WordWrite types")

# 12. Fix WordRead Paragraph
c = c.replace("body.Descendants<Paragraph>().Select(p => p.InnerText)", "body.Descendants<Wordprocessing.Paragraph>().Select(p => p.InnerText)")
print("12 OK: Fixed WordRead Paragraph type")

# 13. Fix ExcelGetStyles types (Font, Fill -> Spreadsheet.*)
c = c.replace("fonts.Cast<Font>().ElementAtOrDefault", "fonts.Cast<Spreadsheet.Font>().ElementAtOrDefault")
c = c.replace("fills.Cast<Fill>().ElementAtOrDefault", "fills.Cast<Spreadsheet.Fill>().ElementAtOrDefault")
print("13 OK: Fixed ExcelGetStyles types")

# 14. Fix EnsureStylesPart types
c = c.replace("new RunPropertiesDefault(new RunProperties(", "new RunPropertiesDefault(new Wordprocessing.RunProperties(")
c = c.replace("new FontSize { Val", "new Wordprocessing.FontSize { Val")
c = c.replace("new ParagraphPropertiesDefault(new ParagraphProperties(", "new ParagraphPropertiesDefault(new Wordprocessing.ParagraphProperties(")
print("14 OK: Fixed EnsureStylesPart types")

# 15. Fix PptGetStyles: D.RunProperties typing
c = c.replace("var rp = run.RunProperties;", "D.RunProperties? rp = run.RunProperties;")
print("15 OK: Fixed PptGetStyles RunProperties typing")

with open(sys.argv[1], "w", encoding="utf-8") as f:
    f.write(c)
print("ALL DONE - file written successfully")
