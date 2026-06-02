with open('src/LTAI.AI/LocalEmbedder.cs', 'rb') as f:
    data = f.read()
lines = data.split(b'\n')
for i in range(768, 774):
    print(f"L{i+1}: {repr(lines[i])}")
