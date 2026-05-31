
import re, sys

with open(sys.argv[1], 'r', encoding='utf-8') as f:
    content = f.read()

lines = content.split(chr(10))
new_lines = [l for l in lines if l.strip() != 'using DocumentFormat.OpenXml.Drawing;']
content = chr(10).join(new_lines)
print('1 OK')

lines = content.split(chr(10))
new_lines = []
for line in lines:
    new_lines.append(line)
    if line.strip() == 'using System.Text;':
        new_lines.append('using System.Text.RegularExpressions;')
content = chr(10).join(new_lines)
print('2 OK')

old = 'var sheetPart = wbPart!.WorksheetParts.First(sp =>' + chr(10) + '                wbPart.GetPartById(sp.GetParentRelationships().First().Id)?.Equals(sp) ?? false);' + chr(10) + '' + chr(10) + '            var data = wbPart.Workbook.Descendants<Sheet>()'
new = 'var data = wbPart!.Workbook.Descendants<Sheet>()'
if old not in content:
    print('FAIL 3: ExcelRead block not found')
    sys.exit(1)
content = content.replace(old, new)
print('3 OK')

old = 'var sheets = tgtWb.Workbook.GetOrCreateSheets();'
new = 'var sheets = tgtWb.Workbook.Sheets ?? tgtWb.Workbook.AppendChild(new Sheets());'
if old not in content:
    print('FAIL 4: GetOrCreateSheets not found')
    sys.exit(1)
content = content.replace(old, new)
print('4 OK')

old = 'new string(cell.CellReference?.ToString()?.TakeWhile(char.IsLetter).ToArray() ?? ' + chr(34) + 'A' + chr(34) + ')'
new = 'new string(cell.CellReference?.ToString()?.TakeWhile(char.IsLetter).ToArray() ?? [])'
if old not in content:
    print('FAIL 5: char[] vs string not found')
    sys.exit(1)
content = content.replace(old, new)
print('5 OK')

old = 'foreach (var cell in srcRow.Elements<Cell>())'
new = 'foreach (var srcCell in srcRow.Elements<Cell>())'
if old not in content:
    print('FAIL 6: loop variable not found')
    sys.exit(1)
content = content.replace(old, new, 1)
print('6 OK')

# Update the rest of cell references in ExcelCopyRange
content = content.replace('cell.DataType?.Value == CellValues.SharedString && cell.CellValue?.Text', 'srcCell.DataType?.Value == CellValues.SharedString && srcCell.CellValue?.Text')
content = content.replace('int.Parse(cell.CellValue.Text)', 'int.Parse(srcCell.CellValue.Text)')
content = content.replace('cell.CellValue?.CloneNode(true) as CellValue', 'srcCell.CellValue?.CloneNode(true) as CellValue')
content = content.replace('cell.DataType?.CloneNode(true) as EnumValue<CellValues>', 'srcCell.DataType?.Value')
print('6b OK')

old = 'tgtStyles.StyleDefinitions?.Save()'
new = 'tgtStyles.Styles?.Save()'
if old not in content:
    print('FAIL 7: StyleDefinitions not found')
    sys.exit(1)
content = content.replace(old, new)
print('7 OK')

with open(sys.argv[1], 'w', encoding='utf-8') as f:
    f.write(content)
print('ALL DONE')
