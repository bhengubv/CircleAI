import re, glob, json

out = []
for path in glob.glob("src/CircleAI.*/**/*DomainContext.cs", recursive=True):
    text = open(path, encoding="utf-8", errors="ignore").read()
    m = re.search(r"class\s+([A-Za-z0-9_]+)DomainContext", text)
    if not m:
        continue
    name = m.group(1)

    snip = re.search(r'SystemPromptSnippet\s*\{\s*get;\s*\}\s*=\s*"((?:[^"\\]|\\.)*)"', text)
    flags = re.search(r'ComplianceFlags\s*\{\s*get;\s*\}\s*=\s*\[([^\]]*)\]', text)
    tools = re.search(r'SuggestedTools\s*\{\s*get;\s*\}\s*=\s*\[([^\]]*)\]', text)

    def items(mm):
        if not mm:
            return []
        return re.findall(r'"((?:[^"\\]|\\.)*)"', mm.group(1))

    if not snip:
        continue

    out.append({
        "name": name,
        "snippet": snip.group(1),
        "flags": items(flags),
        "tools": items(tools),
        "file": path,
    })

out.sort(key=lambda d: d["name"])
print(json.dumps(out, indent=1))
