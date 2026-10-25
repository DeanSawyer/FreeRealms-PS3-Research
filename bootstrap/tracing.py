"""Where the server writes its JSON-lines trace files, and a writer that never breaks serving.

The traces (``ps3_protocol_trace.jsonl``, ``ps3_gateway_trace.jsonl``) are written next to the scripts by default, which is
what ``tools/run_test.py`` reads. Two cases need another place:

* an installed copy (the Windows manager puts the scripts under Program Files, which a normal user cannot write to);
* a folder you choose with the environment variable ``FR_TRACE_DIR``.

If the chosen folder cannot be written, the user's temporary folder is used instead. A failed write is ignored: the trace
is a diagnostic aid and must never turn a client request into an error. Standard library only.
"""
import os
import tempfile
from pathlib import Path


def trace_path(name):
    """Full path of the trace file ``name`` in a folder that can be written."""
    folder = Path(os.environ.get("FR_TRACE_DIR") or Path(__file__).resolve().parent)
    probe = folder / (name + ".probe")
    try:
        folder.mkdir(parents=True, exist_ok=True)
        probe.touch()
        probe.unlink()
    except OSError:
        folder = Path(tempfile.gettempdir())
    return folder / name


def append_line(path, text):
    """Append one line to ``path``; returns False (and does nothing else) when the file cannot be written."""
    try:
        with path.open("a", encoding="utf-8") as out:
            out.write(text + "\n")
        return True
    except OSError:
        return False
