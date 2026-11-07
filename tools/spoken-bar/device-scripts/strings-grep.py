import re, sys
# Prints the printable strings of a binary that match a pattern.
path, pattern = sys.argv[1], sys.argv[2]
data = open(path, 'rb').read()
rx = re.compile(pattern, re.I)
seen = set()
for m in re.finditer(rb'[\x20-\x7e]{4,}', data):
    s = m.group().decode('ascii')
    if rx.search(s) and s not in seen:
        seen.add(s)
        print(hex(m.start()), s[:200])
