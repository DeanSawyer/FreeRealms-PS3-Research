"""Minimal SOE UdpLibrary listener for the PS3 client's game-server (gateway) connection.

After character login the client opens a UDP socket (thread "UdpLibrary") and connects to the address
in CharacterLoginReply/GatewayAddress. This listener completes the UdpLibrary handshake with no CRC and
no encryption, acknowledges reliable packets, and records what the client sends, so the application
protocol on top can be worked out. It is a probe, not a game server: it never sends game data.

Application layer (from the first packets the PS3 client sent, matching Clover's ClientGateway packets):
  Login (client -> server): u16 LE opcode 1 | string ticket | u64 guid | string version | string unknown
  LoginReply (server -> client): u16 LE opcode 2 | u8 success         (strings are u32 LE length + bytes)
The probe answers a Login with LoginReply(success) so the client moves on, then sends the first world packets
(see world_packets) and logs whatever the client sends next.
  Tunneled (either direction): u16 LE opcode 5 | u8 reliable | i32 LE size | inner packet (u16 LE opcode + fields)
  ZoneDetails (inner opcode 43, layout read from the PS3 client): string name | i32 type | u8 | u8 | string sky
                                                                    | u8 in-arena | i32 zone id

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


def _read_string(buf, pos):
    if pos + 4 > len(buf):
        raise ValueError("truncated string length")
    length = struct.unpack_from("<I", buf, pos)[0]
    pos += 4
    if length > 4096 or pos + length > len(buf):
        raise ValueError("bad string length")
    return buf[pos:pos + length].decode("latin-1"), pos + length


def parse_login(payload):
    """Decode a gateway Login packet; returns (ticket, guid, version, unknown) or None."""
    try:
        ticket, pos = _read_string(payload, 2)
        if pos + 8 > len(payload):
            return None
        guid = struct.unpack_from("<Q", payload, pos)[0]
        version, pos = _read_string(payload, pos + 8)
        unknown, pos = _read_string(payload, pos)
        return ticket, guid, version, unknown
    except ValueError:
        return None


def _string(value):
    raw = value.encode("utf-8")
    return struct.pack("<I", len(raw)) + raw


def tunneled(inner, reliable=True):
    return struct.pack("<HBI", 5, 1 if reliable else 0, len(inner)) + inner


def zone_details(name="FabledRealms", zone_type=2, tutorial=0, unknown=0, sky="", arena=0, zone_id=1):
    """Inner packet 43 as the PS3 client (build 1.77) reads it; it must consume every byte."""
    return (struct.pack("<H", 43) + _string(name) + struct.pack("<iBB", zone_type, tutorial, unknown)
            + _string(sky) + struct.pack("<Bi", arena, zone_id))


def world_packets():
    """Packets sent right after the login reply, in order (each already tunneled)."""
    return [tunneled(zone_details())]


class GatewayProbe:
    def __init__(self, host, port):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind((host, port))
        self.host, self.port = host, port
        self.sessions = {}   # addr -> connect code
        self.count = 0
        self.send_seq = {}   # (addr, channel) -> next reliable sequence to send
        self.pending = {}    # (addr, channel, seq) -> [packet, last_send_time, tries]
        self.login_reply = True

    def start(self):
        threading.Thread(target=self.serve, name="gateway-probe", daemon=True).start()
        threading.Thread(target=self.resend_loop, name="gateway-resend", daemon=True).start()
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
        elif 9 <= kind <= 12:                            # Reliable: acknowledge, record, interpret
            channel = kind - 9
            if len(data) >= 4:
                seq = struct.unpack(">H", data[2:4])[0]
                self.sock.sendto(struct.pack(">BBH", 0, 17 + channel, seq), addr)
                self.record(addr, f"{name} seq={seq}", data[4:])
                self.on_application_packet(addr, channel, data[4:])
            else:
                self.record(addr, name, data)
        elif 13 <= kind <= 16:                           # Fragment: acknowledge and record
            channel = kind - 13
            if len(data) >= 4:
                seq = struct.unpack(">H", data[2:4])[0]
                self.sock.sendto(struct.pack(">BBH", 0, 17 + channel, seq), addr)
            self.record(addr, name, data[4:])
        elif 17 <= kind <= 24:                           # Ack / AckAll for what we sent
            channel = (kind - 17) % 4
            if len(data) >= 4:
                seq = struct.unpack(">H", data[2:4])[0]
                for key in [k for k in self.pending if k[0] == addr and k[1] == channel
                            and (k[2] == seq or (kind >= 21 and k[2] <= seq))]:
                    self.pending.pop(key, None)
            LOG.info("gateway %s from %s: %s", name, addr, data[2:].hex())
            _trace("ack", peer=addr[0], label=name, hex=data[2:].hex())
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

    def send_reliable(self, addr, payload, channel=0):
        seq = self.send_seq.get((addr, channel), 0)
        self.send_seq[(addr, channel)] = (seq + 1) & 0xffff
        packet = struct.pack(">BBH", 0, 9 + channel, seq) + payload
        self.pending[(addr, channel, seq)] = [packet, time.monotonic(), 1]
        self.sock.sendto(packet, addr)
        LOG.info("gateway -> %s Reliable%d seq=%d (%d bytes): %s", addr, channel + 1, seq, len(payload),
                 payload[:MAX_LOGGED_BYTES].hex())
        _trace("send", peer=addr[0], channel=channel, seq=seq, hex=payload[:512].hex())

    def resend_loop(self):
        while True:
            time.sleep(0.5)
            now = time.monotonic()
            for key, item in list(self.pending.items()):
                if now - item[1] >= 1.0:
                    if item[2] >= 8:
                        self.pending.pop(key, None)
                        continue
                    item[1], item[2] = now, item[2] + 1
                    try:
                        self.sock.sendto(item[0], key[0])
                    except OSError:
                        return

    def on_application_packet(self, addr, channel, payload):
        if len(payload) < 2:
            return
        opcode = struct.unpack("<H", payload[:2])[0]
        if opcode == 1:                                  # gateway Login
            fields = parse_login(payload)
            if fields is None:
                LOG.warning("gateway Login from %s did not parse as ticket/guid/version/unknown", addr)
                return
            LOG.info("gateway Login: ticket=%r guid=%d version=%r unknown=%r", *fields)
            _trace("login", ticket=fields[0], guid=fields[1], version=fields[2], unknown=fields[3])
            if self.login_reply:
                self.send_reliable(addr, struct.pack("<HB", 2, 1), channel)   # LoginReply(success)
                for packet in world_packets():
                    self.send_reliable(addr, packet, channel)
        elif opcode in (5, 6) and len(payload) >= 7:     # tunneled client packet
            size = struct.unpack_from("<I", payload, 3)[0]
            inner = payload[7:7 + size]
            inner_op = struct.unpack_from("<H", inner, 0)[0] if len(inner) >= 2 else -1
            LOG.info("gateway tunneled (%d) inner opcode %d from %s (%d bytes): %s", opcode, inner_op, addr,
                     len(inner), inner[:MAX_LOGGED_BYTES].hex())
            _trace("tunneled", peer=addr[0], outer=opcode, inner_opcode=inner_op, hex=inner[:512].hex())
        else:
            LOG.info("gateway application opcode %d from %s (%d bytes)", opcode, addr, len(payload))

    def record(self, addr, label, payload):
        shown = payload[:MAX_LOGGED_BYTES]
        LOG.info("gateway %s from %s (%d bytes): %s%s", label, addr, len(payload), shown.hex(),
                 " ..." if len(payload) > len(shown) else "")
        _trace("packet", peer=addr[0], label=label, size=len(payload), hex=payload[:512].hex())
