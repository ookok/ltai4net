import os, re

results = []
for root, dirs, files in os.walk('src/LTAI.Core'):
    dirs[:] = [d for d in dirs if d != 'obj']
    for f in sorted(files):
        if not f.endswith('.cs'):
            continue
        path = os.path.join(root, f)
        with open(path, 'r', encoding='utf-8', errors='replace') as fh:
            content = fh.read()
        lines = content.count('\n')
        decls = []
        for line in content.split('\n'):
            stripped = line.strip()
            m = re.match(r'public\s+(abstract\s+|static\s+|sealed\s+|partial\s+)*(class|record|interface|enum|struct)\b', stripped)
            if m:
                rest = stripped[m.end():]
                name_match = re.search(r'\b([A-Za-z_]\w*)', rest)
                if name_match:
                    decls.append(f'{m.group(2)} {name_match.group(1)}')
        short = path.replace('\\', '/').replace('src/LTAI.Core/', '', 1)
        results.append((short, lines, '; '.join(decls) if decls else '(none)'))

print('| File | Lines | Declarations |')
print('| --- | --- | --- |')
for p, l, d in results:
    print(f'| {p} | {l} | {d} |')
