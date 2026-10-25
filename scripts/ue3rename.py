"""Prototype: rename a cooked big-endian UE3 package to another name of the same length by
rewriting the name-table entries equal to its old name, then re-pack the LZO chunks with
literal-only blocks (valid LZO1X, just larger). Usage: ue3rename.py <in.xxx> <out.xxx>
(the new name is the output file's stem)."""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(__file__))
from ue3names import TAG, expand, fstring, i32, summary, lzo1x_decompress  # noqa: E402


def lzo_literals(data):
    """An LZO1X stream that is one literal run and the end marker."""
    n = len(data)
    out = bytearray()
    if n == 0:
        return bytes([0x11, 0, 0])
    if n <= 238:
        out.append(n + 17)
    else:
        r = n - 18
        k, last = divmod(r, 255)
        if last == 0:
            k, last = k - 1, 255
        out.append(0)
        out += b'\0' * k
        out.append(last)
    out += data
    out += bytes([0x11, 0, 0])
    return bytes(out)


def main():
    src, dst = sys.argv[1], sys.argv[2]
    raw = open(src, 'rb').read()
    s = summary(raw)
    d = bytearray(expand(raw, s))
    # The package's own name: its top-level Package export, as the launcher reads it.
    old = None
    names_at = []
    q = s['no']
    for _ in range(s['nc']):
        at = q
        n, q = fstring(d, q)
        names_at.append((at, n))
        q += 8
    new = os.path.splitext(os.path.basename(dst))[0]
    old = os.path.splitext(os.path.basename(src))[0]
    hits = [(at, n) for at, n in names_at if n.lower() == old.lower()]
    assert hits, f'{old} is not in the name table'
    assert len(new) == len(old), 'only same-length names'
    for at, n in hits:
        length = struct.unpack_from('>i', d, at)[0]
        assert length == len(n) + 1
        d[at + 4:at + 4 + len(new)] = new.encode('latin1')
    print(f'{os.path.basename(src)} -> {new}: {len(hits)} name entr{"y" if len(hits) == 1 else "ies"} rewritten')

    if not s['chunks']:
        open(dst, 'wb').write(d)
        return
    # Re-pack: same uncompressed chunk layout, each chunk as literal-only blocks.
    chunks = sorted(s['chunks'])
    first = chunks[0][0]
    # The physical header is the summary with its chunk table; the expanded view starts the
    # first chunk inside it, so no edited name may fall there.
    assert all(at >= first for at, _ in hits)
    assert min(c[2] for c in chunks) == s['end']
    header = bytearray(raw[:s['end']])
    body = bytearray()
    table = []
    co = s['end']
    block = 0x20000
    for uo, us, _, _ in chunks:
        payload = d[uo:uo + us]
        blocks = [lzo_literals(payload[i:i + block]) for i in range(0, us, block)]
        sizes = [min(block, us - i) for i in range(0, us, block)]
        chunk = bytearray(struct.pack('>IiII', TAG, block, sum(map(len, blocks)), us))
        for b, u in zip(blocks, sizes):
            chunk += struct.pack('>ii', len(b), u)
        for b in blocks:
            chunk += b
        table.append((uo, us, co, len(chunk)))
        body += chunk
        co += len(chunk)
    p = s['chunks_at'] + 4
    for uo, us, c, cs in table:
        struct.pack_into('>iiii', header, p, uo, us, c, cs)
        p += 16
    out = bytes(header + body)
    # Check: expands back to the edited bytes.
    assert expand(out, summary(out)) == bytes(d)
    open(dst, 'wb').write(out)


if __name__ == '__main__':
    main()
