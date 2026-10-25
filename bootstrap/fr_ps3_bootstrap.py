"""Experimental Free Realms PS3 research bootstrap with isolated LZMA terrain response.

Requires a separately obtained PS3 game directory; no proprietary game assets
or original digest bytes are embedded in this source.
"""

import argparse
import json
import time
from datetime import datetime, timezone
import ipaddress
import logging
import socket
import socketserver
import struct
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit, unquote
import shutil
import lzma
import zlib
from urllib.parse import parse_qs

import codec_candidates
import digest_edit


LOG = logging.getLogger("homecoming")
# Largest file for which an LF -> CRLF rewrite is attempted when the CRC does not match.
TEXT_FIX_LIMIT = 64 * 1024 * 1024
DIGEST_ROUTE = "/patch/freerealms/ps3scelive/ps3-web/FreeRealms.soe"
HOSTS = {
    "lpconfig.station.sony.com",
    "frps3.patch.station.sony.com",
    "ps3.freerealms.com",
    "fr.patch.station.sony.com",  # clientconfig.ini [AssetDelivery]
}
SERVER_LIST = (
    b'<?xml version="1.0" encoding="utf-8"?>\r\n'
    b'<ServerListReply><Status>0</Status><StatusMessage>Success</StatusMessage>'
    b'<Servers><Server><Name>NorthForge</Name><Online>1</Online><Locked>0</Locked>'
    b'</Server></Servers></ServerListReply>\r\n'
)
# Experimental variant: one extra Zone field. The value and placement are
# deliberately hypotheses; neither has been verified against the PS3 parser.
SERVER_LIST_ZONE = SERVER_LIST.replace(
    b'<Name>NorthForge</Name>',
    b'<Name>NorthForge</Name><Zone>1</Zone>', 1
)

ACCOUNT_INFO = (
    b'<?xml version="1.0" encoding="utf-8"?>\r\n'
    b'<AccountInfoReply><Status>0</Status><StatusMessage>Success</StatusMessage>'
    b'<Account><Member>0</Member><MaxCharacters>3</MaxCharacters><Admin>0</Admin></Account>'
    b'<Characters><Character><HeadshotUrl></HeadshotUrl><Details><Guid>1</Guid>'
    b'<Name>NorthForge</Name></Details><Model>0</Model><Head></Head><Hair></Hair>'
    b'<ModelCustomization></ModelCustomization><FacePaint></FacePaint>'
    b'<SkinTone></SkinTone><EyeColor>0</EyeColor><HairColor>0</HairColor>'
    b'<Attachments></Attachments></Character></Characters></AccountInfoReply>\r\n'
)
GENERIC_REPLY = (
    b'<?xml version="1.0" encoding="utf-8"?>\r\n'
    b'<Response><Status>0</Status><StatusMessage>Success</StatusMessage></Response>\r\n'
)


def app_config(ip):
    # Preserve the newlines and tag order of the recovered 2026-09-19 script.
    return f"""<AppConfig>
<Login>
<URL type="access">http://{ip}/LaunchPad/lpadAPI/login_v2</URL>
</Login>
<GameList>
<Game env="live">
<Labels/>
<Versions>
<Version id="scea-live-tails">
<Digest>http://frps3.patch.station.sony.com{DIGEST_ROUTE}</Digest>
</Version>
</Versions>
</Game>
</GameList>
</AppConfig>""".encode("utf-8")


def parse_dns_question(packet):
    if len(packet) < 17 or struct.unpack("!H", packet[4:6])[0] != 1:
        raise ValueError("Expected one DNS question")
    pos, labels = 12, []
    while True:
        if pos >= len(packet):
            raise ValueError("Truncated DNS name")
        size = packet[pos]
        pos += 1
        if size == 0:
            break
        if size > 63 or pos + size > len(packet):
            raise ValueError("Invalid DNS label")
        labels.append(packet[pos:pos + size].decode("ascii").lower())
        pos += size
    if pos + 4 > len(packet):
        raise ValueError("Missing DNS query type")
    qtype, qclass = struct.unpack("!HH", packet[pos:pos + 4])
    return ".".join(labels), qtype, qclass, packet[12:pos + 4]


class DNSHandler(socketserver.BaseRequestHandler):
    def handle(self):
        packet, udp = self.request
        try:
            host, qtype, qclass, question = parse_dns_question(packet)
            if host in HOSTS and qclass == 1:
                # An AAAA/HTTPS lookup for a mapped host must also receive a
                # reply. Forwarding it to the dead original DNS often stalled
                # the client's address lookup before any HTTP GET was sent.
                if qtype == 1:
                    answer = (b"\xc0\x0c" + struct.pack("!HHIH", 1, 1, 60, 4)
                              + self.server.answer_ip)
                    count = 1
                else:
                    answer, count = b"", 0  # NOERROR / no data for this type
                header = packet[:2] + b"\x81\x80" + struct.pack("!HHHH", 1, count, 0, 0)
                udp.sendto(header + question + answer, self.client_address)
                LOG.info("DNS %s type=%d -> %s from %s", host, qtype,
                         self.server.advertise if count else "no data", self.client_address[0])
            else:
                # Forward all unrelated queries, including AAAA, to the LAN
                # gateway instead of returning bogus answers.
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as upstream:
                    upstream.settimeout(3)
                    upstream.sendto(packet, (self.server.upstream, 53))
                    reply, _ = upstream.recvfrom(4096)
                udp.sendto(reply, self.client_address)
        except Exception as exc:
            LOG.warning("DNS %s type=%s from %s failed: %s",
                        locals().get("host", "?"), locals().get("qtype", "?"),
                        self.client_address[0], exc)


class DNSServer(socketserver.ThreadingUDPServer):
    allow_reuse_address = True
    daemon_threads = True


TRACE_FILE = Path(__file__).resolve().parent / "ps3_protocol_trace.jsonl"
TRACE_LOCK = threading.Lock()

def trace_event(event, **fields):
    record = {"time_utc": datetime.now(timezone.utc).isoformat(), "event": event, **fields}
    with TRACE_LOCK:
        with TRACE_FILE.open("a", encoding="utf-8") as out:
            out.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n")
    LOG.info("TRACE %s %s", event, " ".join(f"{key}={value}" for key, value in fields.items()))


def make_handler(ip, digest, server_list_variant, client_root,
                 codec=codec_candidates.DEFAULT_CODEC, extra_clients=(), serve_mismatch=False):
    config = app_config(ip)
    asset_root = (client_root / "USRDIR").resolve()
    # Extra game folders searched (in order) for a file whose CRC32 matches.
    asset_roots = [asset_root] + [(Path(p) / "USRDIR").resolve() for p in extra_clients]
    patch_prefix = "/patch/freerealms/ps3scelive/ps3-web/"
    # Which STORED_VARIANTS index the client accepted last (inferred: it moved on to another file).
    stored_state = {"learned": 0, "pending": None}

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"
        server_version = "HomecomingBootstrap/0.2"

        def log_error(self, fmt, *args):
            trace_event("http_connection_error", peer=self.client_address[0],
                        message=(fmt % args)[:400])
            LOG.warning("HTTP CONNECTION %s %s", self.client_address[0], fmt % args)

        def log_message(self, fmt, *args):
            LOG.info("HTTP %s %s", self.client_address[0], fmt % args)

        def respond(self, code, body, content_type, close=False):
            self.send_response(code)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-cache")
            if close:
                self.send_header("Connection", "close")
                self.close_connection = True
            self.end_headers()
            if self.command != "HEAD":
                try:
                    self.wfile.write(body)
                    self.wfile.flush()
                except (ConnectionResetError, BrokenPipeError, ConnectionAbortedError) as exc:
                    trace_event("response_write_failed", path=self.path,
                                exception=type(exc).__name__, peer=self.client_address[0])
                    self.close_connection = True

        def serve_lzma_terrain(self, route):
            # Experiment scoped to the *first* verified PS3 asset.
            # Do not silently alter other asset paths or the original digest.
            relative = unquote(route[len(patch_prefix):]).replace("\\", "/")
            if relative != "FreeRealms.zs/art/environments/terraincalendardefinitions.txt.zs":
                return False

            candidates = [
                asset_root / "art/environments/terraincalendardefinitions.txt",
                asset_root / "FreeRealms.zs/art/environments/terraincalendardefinitions.txt",
                client_root / "art/environments/terraincalendardefinitions.txt",
            ]
            source = next((f for f in candidates if f.is_file()), None)
            if source is None:
                # The game may store its loose patch inputs outside USRDIR.
                # Search only within this isolated client copy.
                matches = list(client_root.rglob("terraincalendardefinitions.txt"))
                source = matches[0] if matches else None
            if source is None:
                trace_event("lzma_source_missing", route=route,
                            candidates=[str(f) for f in candidates])
                self.respond(404, b"Decoded terrain source not found\r\n", "text/plain", close=True)
                return True

            raw = source.read_bytes()
            expected_crc = 0x912D54DE
            if len(raw) != 403 or zlib.crc32(raw) != expected_crc:
                # Known 398-byte LF variant of this *specific* text file.
                corrected = raw.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
                if len(corrected) == 403 and zlib.crc32(corrected) == expected_crc:
                    raw = corrected
            crc = zlib.crc32(raw)
            if len(raw) != 403 or crc != expected_crc:
                trace_event("lzma_source_invalid", source=str(source), size=len(raw), crc32=hex(crc))
                self.respond(409, b"Source does not match original manifest CRC\r\n",
                             "text/plain", close=True)
                return True

            encoded = codec_candidates.encode(codec, raw)
            if codec == "lzma-alone":
                # A meaningful checksum/roundtrip test, NOT a declaration of PS3 compatibility.
                assert lzma.decompress(encoded, format=lzma.FORMAT_ALONE) == raw
            query = parse_qs(urlsplit(self.path).query)
            manifest_length = int(query["l"][0]) if query.get("l", [""])[0].isdigit() else None
            trace_event("lzma_terrain_response", route=route, source=str(source),
                        codec=codec, decoded_bytes=len(raw), decoded_crc32=hex(crc),
                        lzma_bytes=len(encoded), manifest_compressed_bytes=manifest_length,
                        matches_manifest_length=(len(encoded) == manifest_length))
            LOG.warning("EXPERIMENTAL %s: %s encoded=%d bytes, manifest=%s bytes. "
                        "No PS3 codec compatibility is claimed.", codec, source, len(encoded), manifest_length)
            self.respond(200, encoded, "application/octet-stream", close=True)
            return True

        def serve_zs(self, route):
            """Build any FreeRealms.zs/<path>.zs asset from a matching local file.

            The client requests ``?c=<CRC32 of decoded file>&l=<compressed length>``.
            Every configured client folder is searched and the first file whose
            CRC32 matches is used. Hypothesis only: container = zlib (or raw)
            payload + 129-byte trailer.
            """
            relative = unquote(route[len(patch_prefix):]).replace("\\", "/")
            marker = "FreeRealms.zs/"
            if not (relative.startswith(marker) and relative.endswith(".zs")):
                return False
            decoded = relative[len(marker):-3]
            rel_path = Path(decoded)
            if not decoded or rel_path.is_absolute() or ".." in rel_path.parts or "\x00" in decoded:
                self.respond(400, b"Invalid path\r\n", "text/plain", close=True)
                return True
            candidates = []
            for root in asset_roots:
                source = (root / rel_path).resolve()
                try:
                    source.relative_to(root)
                except ValueError:
                    self.respond(403, b"Forbidden\r\n", "text/plain", close=True)
                    return True
                if source.is_file():
                    candidates.append(source)
            query = parse_qs(urlsplit(self.path).query)

            def number(key):
                value = query.get(key, [""])[0]
                return int(value) if value.isdigit() else None

            want_crc, want_len = number("c"), number("l")
            attempt = len(query.get("cb", [""])[0])
            pending = stored_state["pending"]
            if pending and pending[0] != decoded:
                stored_state["learned"], stored_state["pending"] = pending[1], None
                LOG.info("STORED variant %s was accepted (client moved on from %s)",
                         codec_candidates.STORED_VARIANTS[pending[1]][0], pending[0])
            if not candidates:
                trace_event("zs_source_missing", route=route, decoded=decoded,
                            expected_crc32=want_crc, manifest_length=want_len,
                            roots=[str(r) for r in asset_roots])
                self.respond(404, b"Decoded source not found\r\n", "text/plain", close=True)
                return True
            raw, chosen, note, tried = None, None, "", []
            first = None
            for source in candidates:
                data = source.read_bytes()
                if first is None:
                    first = (source, data)
                crc = zlib.crc32(data)
                tried.append({"source": str(source), "size": len(data), "crc32": hex(crc)})
                if want_crc is None or crc == want_crc:
                    raw, chosen = data, source
                    break
                if len(data) <= TEXT_FIX_LIMIT:
                    # Some tools rewrite line endings in text files (LF vs CRLF).
                    fixed = data.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
                    if zlib.crc32(fixed) == want_crc:
                        raw, chosen, note = fixed, source, "lf_to_crlf"
                        break
            if raw is None and serve_mismatch:
                # EXPERIMENT: the local file is a different build than the digest
                # describes. Send it anyway, padded to the length the client expects,
                # to find out whether the client verifies content after downloading.
                chosen, raw = first
                note = "served_crc_mismatch"
                trace_event("zs_serving_mismatch", route=route, decoded=decoded,
                            expected_crc32=hex(want_crc), expected_length=want_len,
                            source=str(chosen), local_size=len(raw),
                            local_crc32=hex(zlib.crc32(raw)))
                LOG.warning("SERVE-MISMATCH %s: sending local copy (%d bytes, crc %s) although "
                            "the client asked for crc %s", decoded, len(raw),
                            hex(zlib.crc32(raw)), hex(want_crc))
            if raw is None:
                trace_event("zs_source_crc_mismatch", route=route, decoded=decoded,
                            expected_crc32=hex(want_crc), tried=tried)
                LOG.warning("No local copy of %s matches client CRC %s; tried %s", decoded,
                            hex(want_crc), [(t["source"], t["size"], t["crc32"]) for t in tried])
                self.respond(409, b"Source does not match client CRC\r\n",
                             "text/plain", close=True)
                return True
            crc = zlib.crc32(raw)
            target = want_len - codec_candidates.ZS_TRAILER_LEN if want_len else None
            variant = ""
            if codec in codec_candidates.ZS_CODECS and target is not None and target == len(raw):
                # Stored (uncompressed) file: vary the container on each client retry.
                index = (stored_state["learned"] + attempt) % len(codec_candidates.STORED_VARIANTS)
                encoded, variant = codec_candidates.encode_stored(index, raw)
                stored_state["pending"] = (decoded, index)
                LOG.info("STORED %s attempt=%d variant=%s", decoded, attempt, variant)
            else:
                encoded = codec_candidates.encode(codec, raw, target)
            trace_event("zs_response", route=route, decoded=decoded, codec=codec, note=note,
                        variant=variant, attempt=attempt,
                        source=str(chosen), decoded_bytes=len(raw), decoded_crc32=hex(crc),
                        bytes=len(encoded), manifest_length=want_len,
                        matches_manifest_length=(len(encoded) == want_len))
            if want_len is not None and len(encoded) != want_len:
                LOG.warning("ZS %s: sent %d bytes, manifest says %d", decoded, len(encoded), want_len)
            self.respond(200, encoded, "application/octet-stream", close=True)
            return True

        def serve_asset(self, route):
            if self.serve_lzma_terrain(route):
                return
            if codec.startswith("zs-") and self.serve_zs(route):
                return
            # Only expose files under the PS3 client's USRDIR, never arbitrary host files.
            relative = unquote(route[len(patch_prefix):])
            parts = Path(relative.replace("\\", "/"))
            if parts.is_absolute() or ".." in parts.parts or not relative or "\x00" in relative:
                self.respond(400, b"Invalid path\r\n", "text/plain", close=True)
                return
            target = (asset_root / parts).resolve()
            try:
                target.relative_to(asset_root)
            except ValueError:
                self.respond(403, b"Forbidden\r\n", "text/plain", close=True)
                return
            if not target.is_file():
                trace_event("asset_missing", route=route, file=relative)
                self.respond(404, b"Asset not found\r\n", "text/plain", close=True)
                return
            size = target.stat().st_size
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(size))
            self.send_header("Cache-Control", "no-cache")
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            trace_event("asset_reply", route=route, bytes=size, method=self.command)
            if self.command == "GET":
                try:
                    with target.open("rb") as data:
                        shutil.copyfileobj(data, self.wfile, length=1024*1024)
                    self.wfile.flush()
                except (ConnectionResetError, BrokenPipeError, ConnectionAbortedError) as exc:
                    trace_event("asset_write_failed", route=route, exception=type(exc).__name__)

        def read_route(self):
            route = urlsplit(self.path).path
            trace_event("http_request", method=self.command, route=route, query=urlsplit(self.path).query,
                        host=self.headers.get("Host", ""), peer=self.client_address[0])
            LOG.info("%s %s host=%s agent=%s", self.command, self.path,
                     self.headers.get("Host", ""), self.headers.get("User-Agent", ""))
            if route == "/app/appconfig.php":
                trace_event("config_reply", route=route, status=200, bytes=len(config),
                            sha256=__import__("hashlib").sha256(config).hexdigest())
                self.respond(200, config, "text/xml; charset=utf-8", close=True)
                LOG.info("200 AppConfig (%d bytes)", len(config))
            elif route == DIGEST_ROUTE:
                trace_event("digest_reply", route=route, status=200, bytes=len(digest),
                            sha256=__import__("hashlib").sha256(digest).hexdigest())
                self.respond(200, digest, "application/octet-stream", close=True)
                LOG.info("200 original PS3 digest (%d bytes)", len(digest))
            elif route.startswith(patch_prefix):
                self.serve_asset(route)
            else:
                trace_event("unhandled_get", method=self.command, route=route,
                            host=self.headers.get("Host", ""), status=404)
                self.respond(404, b"Not implemented\r\n", "text/plain", close=True)

        def do_GET(self):
            self.read_route()

        def do_HEAD(self):
            self.read_route()

        def do_POST(self):
            try:
                length = int(self.headers.get("Content-Length", "0"))
            except ValueError:
                length = -1
            if length < 0 or length > 1024 * 1024:
                self.respond(413, b"Invalid body length\r\n", "text/plain", close=True)
                return
            self.rfile.read(length)  # Never log an account/session payload.
            route = urlsplit(self.path).path
            trace_event("ps3_post", route=route, query=urlsplit(self.path).query,
                        body_bytes=length, peer=self.client_address[0])
            LOG.info("POST %s host=%s body_bytes=%d", self.path,
                     self.headers.get("Host", ""), length)
            if route == "/ps3ws/game/serverList.action":
                reply = SERVER_LIST_ZONE if server_list_variant == "zone" else SERVER_LIST
            elif route == "/ps3ws/account/info.action":
                reply = ACCOUNT_INFO
            elif route.startswith("/ps3ws/"):
                reply = GENERIC_REPLY
            else:
                self.respond(501, b"Not implemented\r\n", "text/plain", close=True)
                return
            trace_event("ps3_reply", route=route, http_status=200,
                        body_bytes=len(reply), reply_kind=("server_list" if route.endswith("serverList.action")
                        else "account_info" if route.endswith("info.action") else "generic"),
                        server_list_variant=(server_list_variant if route.endswith("serverList.action") else None))
            self.respond(200, reply, "text/xml; charset=utf-8", close=True)
            LOG.info("200 test reply for %s (%d bytes)", route, len(reply))

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client", type=Path, required=True, help="NPUA30048 directory")
    parser.add_argument("--server-list-variant", choices=("baseline", "zone"), default="baseline",
                        help="Choose the original XML or experimental Zone response")
    parser.add_argument("--extra-client", type=Path, action="append", default=[], metavar="DIR",
                        help="Additional NPUA30048 folder to search for a file whose CRC32 matches "
                             "what the client requests (repeatable). Only used with --codec zs-*.")
    parser.add_argument("--serve-mismatch", action="store_true",
                        help="EXPERIMENT: when no local file matches the client's CRC32, send the "
                             "local file of that name anyway (padded to the expected length) "
                             "instead of replying 409. Only used with --codec zs-*.")
    parser.add_argument("--match-local", action="store_true",
                        help="EXPERIMENT: serve a digest whose size/CRC/length entries describe the "
                             "files actually in --client, for files that differ from the original "
                             "digest (a different build). The digest's 129-byte trailer is kept "
                             "unchanged, so this only works if the client does not verify it.")
    parser.add_argument("--codec", default=codec_candidates.DEFAULT_CODEC,
                        choices=codec_candidates.codec_names(), metavar="NAME",
                        help="Experimental encoding for the terrain asset (hypothesis only; "
                             "see tools/codec_sizes.py for names). Default keeps the preserved baseline.")
    parser.add_argument("--advertise", default="127.0.0.1", help="IPv4 address PS3 clients connect to (default: 127.0.0.1)")
    parser.add_argument("--upstream", default="1.1.1.1", help="DNS forwarder for unrelated hosts")
    parser.add_argument("--no-dns", action="store_true", help="Use existing Acrylic DNS")
    parser.add_argument("--bind", default="127.0.0.1", help="Listener address: 127.0.0.1 local, 0.0.0.0 for LAN (not a client destination)")
    parser.add_argument("--port", type=int, default=80)
    args = parser.parse_args()
    try:
        address = ipaddress.IPv4Address(args.advertise)
        ipaddress.IPv4Address(args.upstream)
    except ipaddress.AddressValueError as exc:
        parser.error(str(exc))
    path = args.client / "USRDIR" / "Digests" / "NPUA30048.soe"
    digest = path.read_bytes()
    if len(digest) != 21466:
        parser.error(f"Expected original 21466-byte digest; got {len(digest)} at {path}")

    # The digest is a zlib stream followed by a 129-byte trailer; keep the
    # trailer for the --codec zs-zlib-digest-trailer experiment.
    _probe = zlib.decompressobj()
    try:
        _probe.decompress(digest)
        codec_candidates.set_digest_trailer(_probe.unused_data)
    except zlib.error:
        pass

    if args.match_local:
        body, trailer = digest_edit.split_digest(digest)
        new_body, changes = digest_edit.adapt_to_local(body, args.client / "USRDIR")
        if changes:
            digest = digest_edit.pack_digest(new_body, trailer)
        for c in changes:
            trace_event("digest_rewrite", **c)
            print(f"DIGEST-REWRITE {c['path']}: size {c['old_size']} -> {c['new_size']}, "
                  f"crc 0x{c['old_crc32']:08X} -> 0x{c['new_crc32']:08X}, "
                  f"length {c['old_length']} -> {c['new_length']}", flush=True)
        print(f"--match-local: {len(changes)} digest entries rewritten; serving a "
              f"{len(digest)}-byte digest (original 21466)", flush=True)

    trace_event("bootstrap_start", advertise=args.advertise, port=args.port,
                client=str(args.client), digest_bytes=len(digest),
                server_list_variant=args.server_list_variant)
    logging.basicConfig(level=logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s")
    dns = None
    try:
        if not args.no_dns:
            dns = DNSServer((args.bind, 53), DNSHandler)
            dns.answer_ip = address.packed
            dns.advertise = str(address)
            dns.upstream = args.upstream
            threading.Thread(target=dns.serve_forever, daemon=True).start()
            LOG.info("DNS listening on %s:53; hosts -> %s; upstream=%s",
                     args.bind, address, args.upstream)
        with ThreadingHTTPServer((args.bind, args.port), make_handler(str(address), digest, args.server_list_variant, args.client,
                                                  args.codec, args.extra_client,
                                                  args.serve_mismatch)) as http:
            LOG.info("Server-list response variant: %s (experimental Zone field: %s)",
                     args.server_list_variant, args.server_list_variant == "zone")
            LOG.info("HTTP listening on %s:%d; AppConfig=%d bytes; digest=%d bytes",
                     args.bind, args.port, len(app_config(str(address))), len(digest))
            http.serve_forever()
    except OSError as exc:
        LOG.error("Cannot bind DNS/HTTP port: %s", exc)
        raise SystemExit(1) from exc
    except KeyboardInterrupt:
        LOG.info("Stopping")
    finally:
        if dns:
            dns.shutdown()
            dns.server_close()


if __name__ == "__main__":
    main()