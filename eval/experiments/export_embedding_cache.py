#!/usr/bin/env python3
"""
Export embeddings from eval/experiments/out/embed_cache_by_hash.npz to JSON
for consumption by C# chunker_probe and offline benchmark harness.
"""

from pathlib import Path
import json
import numpy as np

def main():
    repo_root = Path(__file__).resolve().parent.parent.parent
    npz_path = repo_root / "eval" / "experiments" / "out" / "embed_cache_by_hash.npz"
    json_path = repo_root / "eval" / "experiments" / "out" / "embed_cache.json"

    if not npz_path.exists():
        print(f"Error: {npz_path} does not exist.")
        return 1

    print(f"Loading {npz_path}...")
    data = np.load(npz_path)
    hashes = [h.decode("utf-8") if isinstance(h, bytes) else str(h) for h in data["hashes"]]
    embeddings = data["embeddings"].tolist()

    cache_dict = {h: emb for h, emb in zip(hashes, embeddings)}
    print(f"Exporting {len(cache_dict)} embedding vectors to {json_path}...")

    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(cache_dict, f)

    file_size_mb = json_path.stat().st_size / (1024 * 1024)
    print(f"Done! Exported {file_size_mb:.2f} MB to {json_path}")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
