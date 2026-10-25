"""Expand a big-endian UE3 package (plain or LZO-chunked) and print its compression, the names
containing a pattern, its top-level imports and exports. Usage: ue3names.py <file.xxx> [pattern]
With --dump <out>, also write the expanded bytes."""
import struct
import sys

TAG = 0x9E2A83C1


def lzo1x_decompress(src, out_len):
    """LZO1X-1 decompression (the format of lzo1x_decompress_safe)."""
    dst = bytearray()
    ip = 0

    def length_ext(ip, base):
        n = 0
        while src[ip] == 0:
            n += 255
            ip += 1
        return base + n + src[ip], ip + 1

    t = src[ip]
    state = 0
    if t > 17:
        ip += 1
        n = t - 17
        dst += src[ip:ip + n]
        ip += n
        state = 4 if n >= 4 else n
        t = src[ip]
    while True:
        t = src[ip]
        ip += 1
        if t < 16:
            if state == 0:
                n = t
                if n == 0:
                    n, ip = length_ext(ip, 15)
                n += 3
                dst += src[ip:ip + n]
                ip += n
                state = 4
                continue
            if state < 4:
                dist = (t >> 2) + (src[ip] << 2) + 1
                ip += 1
                n = 2
            else:
                dist = (t >> 2) + (src[ip] << 2) + 2049
                ip += 1
                n = 3
            nxt = t & 3
        elif t >= 64:
            dist = ((t >> 2) & 7) + (src[ip] << 3) + 1
            ip += 1
            n = (t >> 5) + 1
            nxt = t & 3
        elif t >= 32:
            n = t & 31
            if n == 0:
                n, ip = length_ext(ip, 31)
            n += 2
            v = src[ip] | src[ip + 1] << 8
            ip += 2
            dist = (v >> 2) + 1
            nxt = v & 3
        else:
            n = t & 7
            if n == 0:
                n, ip = length_ext(ip, 7)
            n += 2
            v = src[ip] | src[ip + 1] << 8
            ip += 2
            dist = ((t & 8) << 11) + (v >> 2)
            if dist == 0:
                break  # end of stream
            dist += 16384
            nxt = v & 3
        start = len(dst) - dist
        for k in range(n):
            dst.append(dst[start + k])
        state = nxt
        if nxt:
            dst += src[ip:ip + nxt]
            ip += nxt
    return bytes(dst[:out_len])


def i32(d, p):
    return struct.unpack_from('>i', d, p)[0], p + 4


def fstring(d, p):
    n, p = i32(d, p)
    if n >= 0:
        return d[p:p + max(0, n - 1)].decode('latin1'), p + n
    return d[p:p - 2 * n - 2].decode('utf-16-be'), p - 2 * n


def summary(d):
    s = {}
    _, p = fstring(d, 20)
    s['flags_at'] = p
    for key in ('flags', 'nc', 'no', 'ec', 'eo', 'ic', 'io'):
        s[key], p = i32(d, p)
    p += 16
    gens, p = i32(d, p)
    p += gens * 12 + 8
    s['comp'], p = i32(d, p)
    s['chunks_at'] = p
    count, p = i32(d, p)
    chunks = []
    for _ in range(count):
        uo, p = i32(d, p); us, p = i32(d, p); co, p = i32(d, p); cs, p = i32(d, p)
        chunks.append((uo, us, co, cs))
    s['chunks'] = chunks
    s['end'] = p
    return s


def expand(d, s):
    if not s['chunks']:
        return d
    out = bytearray(d[:min(c[0] for c in s['chunks'])])
    for uo, us, co, cs in sorted(s['chunks']):
        tag, bs, ctot, utot = struct.unpack_from('>IiII', d, co)
        assert tag == TAG, hex(tag)
        p = co + 16
        sizes = []
        total = 0
        while total < utot:
            c, u = struct.unpack_from('>ii', d, p)
            sizes.append((c, u)); total += u; p += 8
        for c, u in sizes:
            out += lzo1x_decompress(d[p:p + c], u)
            p += c
        assert len(out) == uo + us, (len(out), uo, us)
    return bytes(out)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    path = args[0]
    pattern = (args[1] if len(args) > 1 else 'arena_00').lower()
    raw = open(path, 'rb').read()
    if struct.unpack_from('>I', raw, 0)[0] != TAG:
        print(path, 'not a big-endian package')
        return
    s = summary(raw)
    d = expand(raw, s)
    print(f'{path}\n  flags {s["flags"]:#x} names {s["nc"]} exports {s["ec"]} imports {s["ic"]} '
          f'compression {s["comp"]} chunks {len(s["chunks"])} size {len(raw)} expanded {len(d)}')
    if '--dump' in sys.argv:
        open(sys.argv[sys.argv.index('--dump') + 1], 'wb').write(d)
    names = []
    q = s['no']
    for _ in range(s['nc']):
        n, q = fstring(d, q)
        names.append(n)
        q += 8
    print(f'  names with "{pattern}":', [n for n in names if pattern in n.lower()])

    def nm(i, num):
        return names[i] + (f'_{num - 1}' if num else '')
    imports = []
    q = s['io']
    for _ in range(s['ic']):
        q += 8
        cn, q = i32(d, q); cnn, q = i32(d, q)
        outer, q = i32(d, q)
        on, q = i32(d, q); onn, q = i32(d, q)
        imports.append((nm(cn, cnn), outer, nm(on, onn)))
    print('  top-level imports:', [f'{c} {n}' for c, o, n in imports if o == 0])
    tops = []
    q = s['eo']
    for _ in range(s['ec']):
        cls, q = i32(d, q)
        q += 4
        outer, q = i32(d, q)
        n, q = i32(d, q); num, q = i32(d, q)
        q += 4 + 8 + 4 + 4
        cm, q = i32(d, q)
        q += cm * 12 + 4
        net, q = i32(d, q)
        q += net * 4 + 16
        if outer == 0:
            cname = 'Class' if cls == 0 else imports[-cls - 1][2] if cls < 0 else f'export{cls}'
            tops.append(f'{cname} {nm(n, num)}')
    print(f'  top-level exports ({len(tops)}):', tops[:15], '...' if len(tops) > 15 else '')


if __name__ == '__main__':
    main()
