"""Minimal SOE UdpLibrary listener for the PS3 client's game-server (gateway) connection.

After character login the client opens a UDP socket (thread "UdpLibrary") and connects to the address
in CharacterLoginReply/GatewayAddress. This listener completes the UdpLibrary handshake with no CRC and
no encryption, acknowledges reliable packets, and records what the client sends, so the application
protocol on top can be worked out. It is a probe, not a game server: it never sends game data.

Packet layouts follow the UdpLibrary port in the Clover server (Sanctuary.UdpLibrary, AGPL-3.0):
  Connect : 00 01 | protocol version i32 | connect code i32 | max raw packet size i32 | name\\0
  Confirm : 00 02 | connect code i32 | encrypt code i32 | crc bytes u8 | encrypt methods u8 x2
            | max raw packet size i32 | protocol version i32            (all big endian)
  Reliable: 00 09+ch | sequence u16 | payload       Ack: 00 11+ch | sequence u16
"""

import json
import logging
import os
import socket
import struct
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

LOG = logging.getLogger("gateway")
TRACE_FILE = Path(__file__).resolve().parent / "ps3_gateway_trace.jsonl"
PACKET_NAMES = {
    0: "ZeroEscape", 1: "Connect", 2: "Confirm", 3: "Multi", 4: "Big", 5: "Terminate", 6: "KeepAlive",
    7: "ClockSync", 8: "ClockReflect", 9: "Reliable1", 10: "Reliable2", 11: "Reliable3", 12: "Reliable4",
    13: "Fragment1", 14: "Fragment2", 15: "Fragment3", 16: "Fragment4", 17: "Ack1", 18: "Ack2",
    19: "Ack3", 20: "Ack4", 21: "AckAll1", 22: "AckAll2", 23: "AckAll3", 24: "AckAll4", 25: "Group",
    26: "Ordered", 27: "Ordered2", 28: "PortAlive", 29: "UnreachableConnection", 30: "RequestRemap",
}
MAX_LOGGED_BYTES = 160


def _trace(event, **fields):
    record = {"time_utc": datetime.now(timezone.utc).isoformat(), "event": event, **fields}
    with TRACE_FILE.open("a", encoding="utf-8") as out:
        out.write(json.dumps(record) + "\n")


class GatewayProbe:
    def __init__(self, host, port):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind((host, port))
        self.host, self.port = host, port
        self.sessions = {}   # addr -> connect code
        self.count = 0

    def start(self):
        threading.Thread(target=self.serve, name="gateway-probe", daemon=True).start()
        LOG.info("UDP gateway probe listening on %s:%d (handshake + reliable acks, no game data)",
                 self.host, self.port)

    def serve(self):
        while True:
            try:
                data, addr = self.sock.recvfrom(65535)
            except OSError:
                return
            try:
                self.handle(data, addr)
            except Exception as exc:  # a probe must never die on a odd packet
                LOG.warning("gateway packet from %s failed: %s", addr, exc)

    def handle(self, data, addr, depth=0):
        self.count += 1
        if len(data) < 2 or data[0] != 0:
            self.record(addr, "raw", data)
            return
        kind = data[1]
        name = PACKET_NAMES.get(kind, f"type{kind}")
        if kind == 1 and len(data) >= 14:
            version, code, max_raw = struct.unpack(">iii", data[2:14])
            proto = data[14:].split(b"\0", 1)[0].decode("latin-1") if version > 2 else ""
            self.sessions[addr] = code
            LOG.info("gateway Connect from %s: protocol v%d name=%r code=0x%08x max_raw=%d",
                     addr, version, proto, code & 0xffffffff, max_raw)
            _trace("connect", peer=addr[0], version=version, name=proto, code=code, max_raw=max_raw,
                   hex=data[:MAX_LOGGED_BYTES].hex())
            encrypt_code = int.from_bytes(os.urandom(4), "big") & 0x7fffffff
            confirm = struct.pack(">BBiiBBBii", 0, 2, code, encrypt_code, 0, 0, 0, max_raw, 3)
            self.sock.sendto(confirm, addr)
        elif 9 <= kind <= 12 or 13 <= kind <= 16:      # Reliable / Fragment: acknowledge and record
            channel = (kind - 9) % 4
            if len(data) >= 4:
                seq = struct.unpack(">H", data[2:4])[0]
                self.sock.sendto(struct.pack(">BBH", 0, 17 + channel, seq), addr)
                self.record(addr, f"{name} seq={seq}", data[4:])
            else:
                self.record(addr, name, data)
        elif kind == 3 and depth < 3:                    # Multi: [len][packet]...
            pos = 2
            while pos < len(data):
                length = data[pos]
                pos += 1
                if length == 0xff and pos + 2 <= len(data):
                    length = struct.unpack(">H", data[pos:pos + 2])[0]
                    pos += 2
                self.handle(data[pos:pos + length], addr, depth + 1)
                pos += length
        elif kind == 5:
            LOG.info("gateway Terminate from %s: %s", addr, data[2:].hex())
            _trace("terminate", peer=addr[0], hex=data.hex())
        elif kind in (6, 28):
            pass                                          # KeepAlive / PortAlive
        else:
            self.record(addr, name, data)

    def record(self, addr, label, payload):
        shown = payload[:MAX_LOGGED_BYTES]
        LOG.info("gateway %s from %s (%d bytes): %s%s", label, addr, len(payload), shown.hex(),
                 " ..." if len(payload) > len(shown) else "")
        _trace("packet", peer=addr[0], label=label, size=len(payload), hex=payload[:512].hex())
