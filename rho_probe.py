"""Read-only probe for KartRider Rh layer 1.1 / Rho archives.

The vector table is read from the local vendored RhoKey.cs file.  There is no
runtime network dependency: this script can be released and run offline.
It never writes to or executes the sample files.
"""

import os
import re
import struct
import sys
import zlib

LOCAL_RHO_KEY = os.path.join(
    os.path.dirname(__file__),
    "third_party",
    "Kartrider-File-Reader",
    "src",
    "KartriderLibrary",
    "Encrypt",
    "RhoKey.cs",
)


def u32(data, off):
    return struct.unpack_from("<I", data, off)[0]


def i32(data, off):
    return struct.unpack_from("<i", data, off)[0]


def adler(seed, data):
    a = seed & 0xFFFF
    b = (seed >> 16) & 0xFFFF
    for value in data:
        a = (a + value) % 65521
        b = (b + a) % 65521
    return ((b << 16) | a) & 0xFFFFFFFF


def u32_add(*values):
    return sum(values) & 0xFFFFFFFF


def get_vectors(source=LOCAL_RHO_KEY):
    with open(source, "r", encoding="utf-8-sig") as handle:
        text = handle.read()
    start = text.index("private static uint[,] EncryptVectors")
    end = text.index("};", start)
    rows = []
    for row in re.findall(r"\{([^{}]+)\}", text[start:end], re.S):
        vals = [int(x, 16) for x in re.findall(r"0x([0-9a-fA-F]+)", row)]
        if vals:
            rows.append(vals)
    if len(rows) != 4 or any(len(row) != 256 for row in rows):
        raise ValueError(f"unexpected vector table shape: {[len(row) for row in rows]}")
    return rows


def rho_key(name, vectors=None):
    return (adler(0, name.encode("utf-16le")) - 0xA6EE7565) & 0xFFFFFFFF


def vector(vectors, value):
    return (vectors[0][value & 0xFF] ^ vectors[1][(value >> 8) & 0xFF] ^ vectors[2][(value >> 16) & 0xFF] ^ vectors[3][(value >> 24) & 0xFF]) & 0xFFFFFFFF


def decrypt_header(data, key, vectors):
    out = bytearray(len(data))
    current = key
    accumulator = 0
    for off in range(0, len(data) - 3, 4):
        encrypted = u32(data, off)
        plain = encrypted ^ vector(vectors, current) ^ accumulator
        struct.pack_into("<I", out, off, plain & 0xFFFFFFFF)
        accumulator = (accumulator + plain) & 0xFFFFFFFF
        current = (current + 1) & 0xFFFFFFFF
    return bytes(out)


def decrypt_data(data, key):
    stream = []
    cur = (key ^ 0x8473FBC1) & 0xFFFFFFFF
    for _ in range(16):
        stream.append(cur)
        cur = (cur - 0x7B8C043F) & 0xFFFFFFFF
    key_bytes = b"".join(struct.pack("<I", x) for x in stream)
    return bytes(value ^ key_bytes[i & 63] for i, value in enumerate(data))


def read_utf16z(data, pos):
    start = pos
    while pos + 1 < len(data):
        if data[pos] == 0 and data[pos + 1] == 0:
            return data[start:pos].decode("utf-16le", "replace"), pos + 2
        pos += 2
    raise ValueError("unterminated UTF-16 string")


def parse(path, vectors):
    data = open(path, "rb").read()
    base = os.path.splitext(os.path.basename(path))[0]
    identifier = data[:0x40].decode("utf-16le", "replace").rstrip("\0")
    if identifier not in ("Rh layer spec 1.0", "Rh layer spec 1.1"):
        raise ValueError(f"unsupported identifier: {identifier!r}")
    layer = 0 if identifier.endswith("1.0") else 1
    key = rho_key(base)
    header_enc = data[0x80:0x100]
    header = decrypt_header(header_enc, key, vectors) if layer == 1 else decrypt_data(header_enc, key)
    header_info = {
        "checksum": f"0x{u32(header, 0):08X}",
        "checksum_ok": u32(header, 0) == adler(0, header[4:0x80]),
        "version_code": i32(header, 4),
        "block_count": i32(header, 8),
        "whitening_key": f"0x{u32(header, 12):08X}",
        "u1": i32(header, 16),
        "u2": f"0x{u32(header, 20):08X}",
        "data_checksum": f"0x{u32(header, 24):08X}",
        "end_magic": f"0x{u32(header, 28):08X}",
        "end_magic_ok": u32(header, 28) == 0xFC1F9778,
        "u4": i32(header, 32),
    }
    count = header_info["block_count"]
    if count < 1 or count > 100000:
        raise ValueError(f"invalid block count: {count}")
    data_infos = {}
    info_key = (u32(header, 12) ^ key) & 0xFFFFFFFF
    for n in range(count):
        raw = data[0x100 + n * 0x20:0x120 + n * 0x20]
        plain = decrypt_header(raw, (info_key + n) & 0xFFFFFFFF, vectors)
        index = u32(plain, 0)
        data_infos[index] = {
            "index": index,
            "offset": u32(plain, 4) << 8,
            "data_size": i32(plain, 8),
            "uncompressed_size": i32(plain, 12),
            "property": i32(plain, 16),
            "checksum": u32(plain, 20),
        }

    def get_block(index, data_key):
        info = data_infos[index]
        raw = data[info["offset"]:info["offset"] + info["data_size"]]
        prop = info["property"]
        # RhoBlockProperty: Compressed=2, PartialEncrypted=4,
        # FullEncrypted=5, CompressedEncrypted=7.
        if prop & 2:
            try:
                raw = zlib.decompress(raw)
            except zlib.error as exc:
                raise ValueError(f"zlib block {index} info={info} head={raw[:16].hex()} error={exc}") from exc
        if prop & 4:
            raw = decrypt_data(raw, data_key)
        if prop == 5 and index + 1 in data_infos:
            second = data_infos[index + 1]
            raw += data[second["offset"]:second["offset"] + second["data_size"]]
        return raw

    folder_key = (key + 0x2593A9F1) & 0xFFFFFFFF
    folders = []
    files = []
    queue = [(0xFFFFFFFF, "")]
    seen_folders = set()
    while queue:
        folder_index, folder_path = queue.pop(0)
        if folder_index in seen_folders:
            continue
        seen_folders.add(folder_index)
        folder_data = get_block(folder_index, folder_key)
        pos = 0
        folder_count = i32(folder_data, pos)
        pos += 4
        subfolders = []
        for _ in range(folder_count):
            name, pos = read_utf16z(folder_data, pos)
            child_index = u32(folder_data, pos)
            pos += 4
            subfolders.append((name, child_index))
        file_count = i32(folder_data, pos)
        pos += 4
        for _ in range(file_count):
            file_name, pos = read_utf16z(folder_data, pos)
            ext_num = u32(folder_data, pos)
            prop = i32(folder_data, pos + 4)
            index = u32(folder_data, pos + 8)
            file_size = i32(folder_data, pos + 12)
            pos += 16
            ext = struct.pack("<I", ext_num).rstrip(b"\0").decode("ascii", "replace")
            files.append({"path": (folder_path + "/" if folder_path else "") + file_name + ("." + ext if ext else ""), "name": file_name, "extension": ext, "property": prop, "index": index, "size": file_size})
        folders.append({"path": folder_path, "index": folder_index, "subfolders": subfolders, "file_count": file_count})
        for child_name, child_index in subfolders:
            child_path = (folder_path + "/" if folder_path else "") + child_name
            queue.append((child_index, child_path))

    return {"file": os.path.basename(path), "size": len(data), "identifier": identifier, "rho_key": f"0x{key:08X}", "header": header_info, "folders": folders, "files": files, "data_info_count": len(data_infos), "data_infos": list(data_infos.values())}


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("usage: rho_probe.py FILE...")
    vectors = get_vectors()
    for path in sys.argv[1:]:
        try:
            result = parse(path, vectors)
            print(result)
        except Exception as exc:
            print({"file": os.path.basename(path), "error": f"{type(exc).__name__}: {exc}"})
