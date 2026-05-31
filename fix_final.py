import sys

with open(sys.argv[1], "r", encoding="utf-8") as f:
    c = f.read()

# Fix Font ambiguity
c = c.replace("fonts.Cast<Font>().ElementAtOrDefault", "fonts.Cast<DocumentFormat.OpenXml.Spreadsheet.Font>().ElementAtOrDefault")
print("1 OK")

# Fix Run/Text ambiguity in WordWrite
c = c.replace("new Run(new Text(t[2..])", "new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(t[2..])")
c = c.replace("new Run(new Text(line)))", "new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(line)))")
c = c.replace("new Run(new Text(line))));", "new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(line))));")
print("2 OK")

# Fix RunProperties ambiguity
c = c.replace("new RunPropertiesDefault(new RunProperties(", "new RunPropertiesDefault(new DocumentFormat.OpenXml.Wordprocessing.RunProperties(")
print("3 OK")

# Fix FontSize ambiguity
c = c.replace("new FontSize { Val", "new DocumentFormat.OpenXml.Wordprocessing.FontSize { Val")
print("4 OK")

# Fix ParagraphProperties ambiguity
c = c.replace("new ParagraphPropertiesDefault(new ParagraphProperties(", "new ParagraphPropertiesDefault(new DocumentFormat.OpenXml.Wordprocessing.ParagraphProperties(")
print("5 OK")

# Fix SolidFill issue
c = c.replace("D.RunProperties? rp = run.RunProperties;", "var rp = (D.RunProperties)run.RunProperties;")
print("6 OK")

# Fix TextTools.cs
with open(r"F:\\mhzyapp\\ltai4net\\src\\LTAI.Agent\\Tools\\TextTools.cs", "r", encoding="utf-8") as ft:
    ct = ft.read()
ct = ct.replace("if (idx != last)", "if (first != last)", 1)
with open(r"F:\\mhzyapp\\ltai4net\\src\\LTAI.Agent\\Tools\\TextTools.cs", "w", encoding="utf-8") as ft:
    ft.write(ct)
print("7 OK")

with open(sys.argv[1], "w", encoding="utf-8") as f:
    f.write(c)
print("ALL DONE")
