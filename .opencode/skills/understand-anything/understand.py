import os, json, sys

def main():
    """Build interactive knowledge graph for the current project."""
    project_root = os.getcwd()
    output_dir = os.path.join(project_root, ".understand-anything")
    os.makedirs(output_dir, exist_ok=True)

    nodes = []
    edges = []
    seen_ids = set()

    for root, dirs, files in os.walk(project_root):
        dirs[:] = [d for d in dirs if d not in (".git", "bin", "obj", "node_modules", ".understand-anything")]
        for f in files:
            if f.endswith((".cs", ".csproj", ".json", ".md", ".sln", ".py", ".js", ".ts", ".jsx", ".tsx", ".html", ".css", ".scss", ".xml", ".yaml", ".yml", ".toml", ".java", ".go", ".rs", ".rb", ".cpp", ".c", ".h", ".swift", ".kt", ".sh", ".bat", ".ps1", ".vue", ".svelte")):
                fpath = os.path.relpath(os.path.join(root, f), project_root)
                fid = fpath.replace("\\", "/")
                if fid in seen_ids:
                    continue
                seen_ids.add(fid)
                ext = os.path.splitext(f)[1]
                group = {"cs": "code", "java": "code", "go": "code", "rs": "code", "rb": "code",
                         "cpp": "code", "c": "code", "h": "code", "swift": "code", "kt": "code",
                         "csproj": "config", "json": "config", "sln": "config",
                         "xml": "config", "yaml": "config", "yml": "config", "toml": "config",
                         "md": "doc", "rst": "doc",
                         "py": "script", "sh": "script", "bat": "script", "ps1": "script",
                         "js": "code", "ts": "code", "jsx": "code", "tsx": "code",
                         "html": "ui", "css": "ui", "scss": "ui",
                         "vue": "ui", "svelte": "ui"}.get(ext, "other")
                nodes.append({"id": fid, "label": f, "group": group, "path": fid, "ext": ext})
                parent = os.path.dirname(fid)
                if parent and parent != ".":
                    edges.append({"from": parent, "to": fid})

    graph = {"nodes": nodes, "edges": edges, "project": os.path.basename(project_root),
             "total_files": len(nodes), "generated_at": __import__("datetime").datetime.utcnow().isoformat()}
    graph_path = os.path.join(output_dir, "knowledge-graph.json")
    with open(graph_path, "w", encoding="utf-8") as fp:
        json.dump(graph, fp, ensure_ascii=False, indent=2)
    print(json.dumps({"status": "ok", "nodes": len(nodes), "edges": len(edges), "path": graph_path}))

if __name__ == "__main__":
    main()
