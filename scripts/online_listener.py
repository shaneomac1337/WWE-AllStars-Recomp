"""UDP listener for the online spike.

Binds the given UDP ports on all interfaces and logs every datagram in hex, with
a Quazal PRUDP v0 header decode. By default it never replies.

With --respond it also answers the PRUDP v0 handshake (SYN, CONNECT) and acknowledges
DATA packets, and decodes the RMC request inside (RC4 with Quazal's default key). It
never answers an RMC call: it shows what the game asks first, it is not the game server.
Checksums need the title's access key, read from [Quazal] AccessKey in the game's own
Coalesced.ini (--ini); the key is never printed or stored.

    python scripts/online_listener.py [--ports 32480] [--log analysis/online-listener.log]
        [--respond --ini assets/Game/Config/Xenon/Cooked/Coalesced.ini]
"""

from __future__ import annotations

import argparse
import datetime
import os
import re
import selectors
import socket
import struct
import sys
from pathlib import Path

# PRUDP v0 packet types (low 3 bits of the type/flags byte) and flags (high 5 bits).
SYN, CONNECT, DATA, DISCONNECT, PING = range(5)
PACKET_TYPES = {SYN: "SYN", CONNECT: "CONNECT", DATA: "DATA", DISCONNECT: "DISCONNECT", PING: "PING"}
ACK, RELIABLE, NEED_ACK, HAS_SIZE = 0x01, 0x02, 0x04, 0x08
FLAGS = {ACK: "ACK", RELIABLE: "RELIABLE", NEED_ACK: "NEED_ACK", HAS_SIZE: "HAS_SIZE"}
# Quazal's default stream key for unsecured RV streams.
DEFAULT_RC4_KEY = b"CD&ML"


def checksum(key_sum: int, data: bytes) -> int:
    """PRUDP v0 (Quazal) checksum: key byte sum + trailing bytes + bytes of the word sum."""
    words = struct.unpack_from("<%dI" % (len(data) // 4), data)
    total = sum(words) & 0xFFFFFFFF
    return (key_sum + sum(data[len(data) & ~3 :]) + sum(struct.pack("<I", total))) & 0xFF


class Rc4:
    def __init__(self, key: bytes) -> None:
        self.s = list(range(256))
        j = 0
        for i in range(256):
            j = (j + self.s[i] + key[i % len(key)]) & 0xFF
            self.s[i], self.s[j] = self.s[j], self.s[i]
        self.i = self.j = 0

    def crypt(self, data: bytes) -> bytes:
        out = bytearray()
        for byte in data:
            self.i = (self.i + 1) & 0xFF
            self.j = (self.j + self.s[self.i]) & 0xFF
            self.s[self.i], self.s[self.j] = self.s[self.j], self.s[self.i]
            out.append(byte ^ self.s[(self.s[self.i] + self.s[self.j]) & 0xFF])
        return bytes(out)


def parse_v0(data: bytes) -> dict | None:
    """Header fields of a PRUDP v0 packet, or None if it's too short."""
    if len(data) < 11:
        return None
    kind, flags = data[2] & 0x07, data[2] >> 3
    packet = {
        "src": data[0], "dst": data[1], "type": kind, "flags": flags, "session": data[3],
        "signature": data[4:8], "seq": int.from_bytes(data[8:10], "little"),
    }
    offset = 10
    if kind in (SYN, CONNECT):
        packet["conn_signature"] = data[offset : offset + 4]
        offset += 4
    if kind == DATA:
        packet["fragment"] = data[offset]
        offset += 1
    if flags & HAS_SIZE:
        offset += 2
    packet["payload"] = data[offset:-1]
    return packet


def describe(packet: dict | None) -> str:
    if packet is None:
        return "too short for PRUDP v0"
    flags = [name for bit, name in FLAGS.items() if packet["flags"] & bit]
    kind = PACKET_TYPES.get(packet["type"], f"type{packet['type']}")
    text = (
        f"src={packet['src'] >> 4}:{packet['src'] & 0xF} dst={packet['dst'] >> 4}:{packet['dst'] & 0xF} "
        f"{kind}{'|' + '|'.join(flags) if flags else ''} session={packet['session']:#04x} "
        f"signature={packet['signature'].hex()} seq={packet['seq']}"
    )
    if "conn_signature" in packet:
        text += f" conn_signature={packet['conn_signature'].hex()}"
    if packet["payload"]:
        text += f" payload={len(packet['payload'])} bytes"
    return text


def hexdump(data: bytes) -> str:
    lines = []
    for offset in range(0, len(data), 16):
        chunk = data[offset : offset + 16]
        text = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
        lines.append(f"  {offset:04x}  {chunk.hex(' '):<47}  {text}")
    return "\n".join(lines)


def access_key_sum(ini: Path) -> int:
    """Byte sum of the active [Quazal] AccessKey in a UE3 Coalesced.ini."""
    text = ini.read_bytes().decode("latin-1")
    for match in re.finditer(r"(;?)\s*AccessKey\s*=\s*([^\r\n\x00]+)", text):
        if not match.group(1):
            return sum(match.group(2).strip().encode()) & 0xFF
    raise SystemExit(f"no active AccessKey in {ini}")


class Responder:
    """Per-client PRUDP v0 handshake state; acknowledges, never answers RMC."""

    def __init__(self, key_sum: int) -> None:
        self.key_sum = key_sum
        self.clients: dict[tuple, dict] = {}

    def build(self, src: int, dst: int, kind: int, flags: int, session: int, signature: bytes,
              seq: int, conn_signature: bytes | None = None, fragment: int | None = None) -> bytes:
        header = bytes([src, dst, (flags << 3) | kind, session]) + signature + seq.to_bytes(2, "little")
        if conn_signature is not None:
            header += conn_signature
        if fragment is not None:
            header += bytes([fragment])
        return header + bytes([checksum(self.key_sum, header)])

    def handle(self, address: tuple, packet: dict) -> tuple[list[bytes], str | None]:
        """Replies to send, and a decoded RMC line for DATA."""
        client = self.clients.setdefault(address, {
            "server_signature": os.urandom(4), "session": 0x5A, "rc4_in": Rc4(DEFAULT_RC4_KEY),
            "client_signature": bytes(4),
        })
        src, dst = packet["dst"], packet["src"]  # replies swap the endpoints
        kind, seq = packet["type"], packet["seq"]
        if kind == SYN and not packet["flags"] & ACK:
            return [self.build(src, dst, SYN, ACK, 0, bytes(4), seq, client["server_signature"])], None
        if kind == CONNECT and not packet["flags"] & ACK:
            client["client_signature"] = packet["conn_signature"]
            client["rc4_in"] = Rc4(DEFAULT_RC4_KEY)
            return [self.build(src, dst, CONNECT, ACK, client["session"], client["client_signature"], seq,
                               bytes(4))], None
        if kind == DATA and not packet["flags"] & ACK:
            ack = self.build(src, dst, DATA, ACK, client["session"], client["client_signature"], seq,
                             fragment=0)
            # The RC4 stream advances once per packet: a retransmit is not decrypted again.
            seen = client.setdefault("seen", set())
            if seq in seen:
                return [ack], f"(retransmit of seq {seq})"
            seen.add(seq)
            return [ack], self.decode_rmc(client["rc4_in"].crypt(packet["payload"]))
        if kind == PING and packet["flags"] & NEED_ACK:
            return [self.build(src, dst, PING, ACK, client["session"], client["client_signature"], seq)], None
        return [], None

    @staticmethod
    def decode_rmc(payload: bytes) -> str:
        """v0 DATA payload: a compression-ratio byte (0 = none), then the RMC message:
        u32 size, u8 protocol|0x80, u32 call id, u32 method id, parameters."""
        if len(payload) < 14 or payload[0] != 0:
            return f"compressed or short payload {payload.hex(' ')}"
        plain = payload[1:]
        size, protocol, call_id, method = struct.unpack_from("<IBII", plain)
        request = "request" if protocol & 0x80 else "response"
        params = plain[13:]
        strings = re.findall(rb"[\x20-\x7e]{3,}", params)
        return (f"RMC {request} protocol={protocol & 0x7F:#04x} method={method:#x} call={call_id} "
                f"size={size} params={len(params)} bytes strings={[s.decode() for s in strings]}\n"
                + hexdump(plain))


def main() -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("--ports", default="32480,11705", help="comma-separated UDP ports")
    parser.add_argument("--log", default="analysis/online-listener.log", help="log file")
    parser.add_argument("--respond", action="store_true", help="answer the PRUDP handshake")
    parser.add_argument("--ini", default="assets/Game/Config/Xenon/Cooked/Coalesced.ini",
                        help="the game's Coalesced.ini, for the access key (--respond only)")
    args = parser.parse_args()

    responder = Responder(access_key_sum(Path(args.ini))) if args.respond else None
    log_path = Path(args.log)
    log_path.parent.mkdir(parents=True, exist_ok=True)
    selector = selectors.DefaultSelector()
    for port in (int(p) for p in args.ports.split(",") if p.strip()):
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.bind(("0.0.0.0", port))
        sock.setblocking(False)
        selector.register(sock, selectors.EVENT_READ, port)

    with log_path.open("a", encoding="utf-8") as log:

        def write(line: str) -> None:
            print(line, flush=True)
            log.write(line + "\n")
            log.flush()

        mode = "responding to the handshake" if responder else "log only"
        write(f"# listening on UDP {args.ports} ({mode}) at {datetime.datetime.now().isoformat()}")
        try:
            while True:
                for key, _ in selector.select():
                    sock: socket.socket = key.fileobj  # type: ignore[assignment]
                    data, address = sock.recvfrom(65535)
                    stamp = datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]
                    packet = parse_v0(data)
                    write(f"{stamp} :{key.data} <- {address[0]}:{address[1]} {len(data)} bytes; {describe(packet)}")
                    write(hexdump(data))
                    if responder and packet:
                        replies, rmc = responder.handle(address, packet)
                        if rmc:
                            write(f"  {rmc}")
                        for reply in replies:
                            sock.sendto(reply, address)
                            write(f"  -> {describe(parse_v0(reply))}")
        except KeyboardInterrupt:
            pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
