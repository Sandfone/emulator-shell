#!/usr/bin/env python3
"""Looks through a SwiftPM build folder for async functions that several modules define under one symbol
name with async frames of different sizes: the linker keeps one definition, and a caller in another module,
which reserved its own module's size, then runs it in a frame of the wrong size. That has aborted the app
in swift_task_dealloc ("freed pointer was not the last allocation"); see `Task.pause(for:)`.

Usage: check-async-symbols.py <folder with the *.build folders>   (exit status 1 if any is found)"""
import collections, glob, re, struct, subprocess, sys


def section(obj, segment, name):
    """The bytes of a section, by address."""
    memory = {}
    dump = subprocess.run(["otool", "-X", "-s", segment, name, obj], capture_output=True, text=True).stdout
    for line in dump.splitlines():
        parts = line.split()
        if len(parts) < 2:
            continue
        address = int(parts[0], 16)
        for word in parts[1:]:
            data = bytes.fromhex(word)
            # otool prints some sections as little-endian 32-bit words.
            for byte in (data[::-1] if len(data) == 4 else data):
                memory[address] = byte
                address += 1
    return memory


def main(folder):
    # An async function pointer (symbol suffix "Tu") is {relative code offset, frame size}, 32 bits each.
    definitions = collections.defaultdict(set)
    for obj in glob.glob(folder + "/*.build/**/*.o", recursive=True):
        module = re.search(r"/([^/]+)\.build/", obj).group(1)
        symbols = subprocess.run(["nm", "-m", obj], capture_output=True, text=True).stdout
        sections = {}
        for line in symbols.splitlines():
            if not line.endswith("Tu") or "undefined" in line or "non-external" in line or "external" not in line:
                continue
            where = re.search(r"\((\w+),(\w+)\)", line)
            if not where:
                continue
            if where.groups() not in sections:
                sections[where.groups()] = section(obj, *where.groups())
            address = int(line.split()[0], 16) + 4
            size = struct.unpack("<I", bytes(sections[where.groups()].get(address + i, 0) for i in range(4)))[0]
            definitions[line.split()[-1]].add((module, size))
    conflicts = {s: d for s, d in definitions.items() if len({size for _, size in d}) > 1}
    for symbol, found in sorted(conflicts.items()):
        name = subprocess.run(["xcrun", "swift-demangle", "--simplified", symbol[1:]], capture_output=True, text=True)
        print(f"error: {name.stdout.strip() or symbol}", file=sys.stderr)
        for module, size in sorted(found):
            print(f"    {module}: {size} bytes", file=sys.stderr)
    if conflicts:
        print("These async functions are defined in several modules with frames of different sizes; the linker "
              "keeps one, and callers in the other modules will corrupt their task's memory. Keep this app's "
              "code from calling them (see Task.pause(for:) in EmulatorClient.swift).", file=sys.stderr)
    return 1 if conflicts else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
