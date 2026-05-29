import re, os

for root, dirs, files in os.walk('src/LTAI.Core'):
    dirs[:] = [d for d in dirs if d != 'obj']
    for f in sorted(files):
        if not f.endswith('.cs'):
            continue
        path = os.path.join(root, f)
        with open(path, 'r', encoding='utf-8', errors='replace') as fh:
            content = fh.read()
        nonpublic = []
        for line in content.split('\n'):
            stripped = line.strip()
            m = re.match(r'(?:public|internal|private|protected|)\s*(?:abstract\s+|static\s+|sealed\s+|partial\s+)*(class|record|interface|enum|struct)\b', stripped)
            if m and not stripped.startswith('public'):
                rest = stripped[m.end():]
                name_match = re.search(r'\b([A-Za-z_]\w*)', rest)
                if name_match:
                    nonpublic.append(f'{name_match.group(1)}')
        if nonpublic:
            short = path.replace('\\', '/').replace('src/LTAI.Core/', '', 1)
            print(f'{short}: {"; ".join(nonpublic)}')
