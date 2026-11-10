"""Writes the same test folder on every machine (fixed seed), for the UDF conformance workflow.

Usage: python make-udf-test-tree.py <output folder>

Covers what tends to break UDF readers: Latin-1 and CJK names, a name longer than Joliet allows, nesting, an empty
file, a directory with enough entries to span several sectors, and a file of a few megabytes.
"""
import os
import random
import sys

out = sys.argv[1]
rng = random.Random(20261110)


def write(rel, size):
    path = os.path.join(out, *rel.split("/"))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(rng.randbytes(size))


write("empty.txt", 0)
write("big.bin", 3_000_000)
write("Ordner ü/notes été.txt", 4_321)
write("目录/文件.dat", 70_000)
write("a/b/c/d/e/deep.bin", 9_999)
write("long/" + "a-file-name-longer-than-joliet-allows-" * 3 + "end.txt", 1_234)
for i in range(120):
    write(f"many/entry {i:03d} with a reasonably long name.bin", rng.randrange(1, 5_000))
print("tree written to", out)
