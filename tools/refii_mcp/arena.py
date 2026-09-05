"""Out-of-process guest memory: the gdb-equivalent path.

The guest's whole address space is one named shared-memory object (`Memory::Initialize`,
`xemory.cpp`: `xenia_memory_<tick>`, 4GB+512MB). That object is readable by any same-user
process WITHOUT ptrace, which buys the three things the debug channel can never have: it works
while the game is FROZEN (the channel is served from the host's main thread), it works on a
DEAD run (Linux: crashed runs leak their arenas in /dev/shm, each one a full post-mortem
image), and it perturbs nothing.

Two backends:

- POSIX: the /dev/shm file, read by offset. Guest address -> file offset goes through the view
  table below, copied from xmemory.cpp `map_info[]` — the mapping is NOT identity everywhere.
- Windows: ReadProcessMemory at membase + guest address, straight through the game's own page
  tables (so the aliases cost nothing). Mapping the named section ourselves would be wrong: it
  is SEC_RESERVE, and reading an uncommitted page of a mapped view faults, while RPM fails
  cleanly per page. No post-mortem on Windows — the mapping dies with its last handle.

The arena's name and membase come from the handshake (published by GameDebugServer the moment
the guest is up — BEFORE any freeze), with /proc/<pid>/maps as the Linux fallback for builds
that predate the field.
"""
import ctypes
import glob
import os
import sys

from .channel import handshake_info

# xmemory.cpp map_info: (guest_start, guest_end inclusive, file target). 0x90000000 aliases the
# XEX file range; 0x7F000000, 0xA0000000, 0xC0000000 and 0xE0000000 are all windows onto the
# physical pages at file offset 0x100000000 (0xE0000000 skewed +0x1000).
GUEST_VIEWS = [
    (0x00000000, 0x3FFFFFFF, 0x000000000),
    (0x40000000, 0x7EFFFFFF, 0x040000000),
    (0x7F000000, 0x7FFFFFFF, 0x100000000),
    (0x80000000, 0x8FFFFFFF, 0x080000000),
    (0x90000000, 0x9FFFFFFF, 0x080000000),
    (0xA0000000, 0xBFFFFFFF, 0x100000000),
    (0xC0000000, 0xDFFFFFFF, 0x100000000),
    (0xE0000000, 0xFFFFFFFF, 0x100001000),
]

# The alias-free subset that covers every distinct byte once — what `find` scans. Physical
# memory is reported in its 0xA0000000 window; the same bytes exist at 0x7F/0xC0/0xE0 aliases.
SEARCH_VIEWS = [
    (0x00000000, 0x3FFFFFFF),
    (0x40000000, 0x7EFFFFFF),
    (0x80000000, 0x8FFFFFFF),
    (0xA0000000, 0xBFFFFFFF),
]


def file_offset(addr):
    """The shm-file offset holding guest byte `addr`, or None outside every view."""
    for start, end, target in GUEST_VIEWS:
        if start <= addr <= end:
            return target + (addr - start)
    return None


class ArenaFileReader:
    """POSIX: the /dev/shm object, live or leaked. Holes read as zeros, like untouched RAM."""

    def __init__(self, path):
        self.path = path
        self.fd = os.open(path, os.O_RDONLY)

    def close(self):
        os.close(self.fd)

    def read(self, addr, count):
        """Up to `count` bytes from `addr`, spliced across view boundaries."""
        pieces = []
        while count > 0:
            hit = next(((s, e, t) for s, e, t in GUEST_VIEWS if s <= addr <= e), None)
            if hit is None:
                break
            start, end, target = hit
            span = min(count, end + 1 - addr)
            pieces.append(os.pread(self.fd, span, target + (addr - start)))
            addr += span
            count -= span
        return b"".join(pieces)

    def data_runs(self, lo, hi):
        """(guest_addr, length) runs that are actually RESIDENT between guest lo..hi.

        SEEK_DATA against the sparse file is what makes scanning a "4.5 GB" arena cost only
        the ~hundreds of MB the run ever touched.
        """
        while lo < hi:
            offset = file_offset(lo)
            if offset is None:
                break
            _, view_end, _ = next(v for v in GUEST_VIEWS if v[0] <= lo <= v[1])
            view_hi = min(hi, view_end + 1)
            try:
                data = os.lseek(self.fd, offset, os.SEEK_DATA)
            except OSError:  # no data at or after offset in the file
                lo = view_hi
                continue
            data_addr = lo + (data - offset)
            if data_addr >= view_hi:
                lo = view_hi
                continue
            hole = os.lseek(self.fd, data, os.SEEK_HOLE)
            run_end = min(view_hi, data_addr + (hole - data))
            yield data_addr, run_end - data_addr
            lo = run_end


class ProcessMemoryReader:
    """Windows: ReadProcessMemory at membase + guest address. Needs the pid alive (a FROZEN
    process is alive); a dead one has no memory to read — Windows keeps no post-mortem."""

    PROCESS_VM_READ = 0x0010
    PROCESS_QUERY_INFORMATION = 0x0400
    MEM_COMMIT = 0x1000
    PAGE_NOACCESS = 0x01
    PAGE_GUARD = 0x100

    class MemoryBasicInformation(ctypes.Structure):
        _fields_ = [
            ("BaseAddress", ctypes.c_uint64),
            ("AllocationBase", ctypes.c_uint64),
            ("AllocationProtect", ctypes.c_uint32),
            ("PartitionId", ctypes.c_uint16),
            ("RegionSize", ctypes.c_uint64),
            ("State", ctypes.c_uint32),
            ("Protect", ctypes.c_uint32),
            ("Type", ctypes.c_uint32),
        ]

    def __init__(self, pid, membase):
        self.pid = pid
        self.membase = membase
        kernel32 = ctypes.windll.kernel32
        kernel32.OpenProcess.restype = ctypes.c_void_p
        kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
        kernel32.ReadProcessMemory.argtypes = [
            ctypes.c_void_p, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_size_t,
            ctypes.c_void_p]
        kernel32.VirtualQueryEx.argtypes = [
            ctypes.c_void_p, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_size_t]
        kernel32.VirtualQueryEx.restype = ctypes.c_size_t
        self.kernel32 = kernel32
        self.handle = kernel32.OpenProcess(
            self.PROCESS_VM_READ | self.PROCESS_QUERY_INFORMATION, False, int(pid))
        if not self.handle:
            raise OSError(f"OpenProcess({pid}) failed ({kernel32.GetLastError()})")

    def close(self):
        self.kernel32.CloseHandle(self.handle)

    def read(self, addr, count):
        """Bytes at guest `addr`. A range crossing an uncommitted page is re-read per page
        with zero fill, matching the file backend's holes-read-as-zeros semantics."""
        buffer = ctypes.create_string_buffer(count)
        got = ctypes.c_size_t()
        if self.kernel32.ReadProcessMemory(
                self.handle, ctypes.c_uint64(self.membase + addr), buffer,
                ctypes.c_size_t(count), ctypes.byref(got)):
            return buffer.raw[: got.value]

        page = 4096
        pieces = []
        offset = 0
        while offset < count:
            step = min(page - ((addr + offset) % page), count - offset)
            chunk = ctypes.create_string_buffer(step)
            if self.kernel32.ReadProcessMemory(
                    self.handle, ctypes.c_uint64(self.membase + addr + offset), chunk,
                    ctypes.c_size_t(step), ctypes.byref(got)) and got.value == step:
                pieces.append(chunk.raw)
            else:
                pieces.append(b"\x00" * step)
            offset += step
        return b"".join(pieces)

    def data_runs(self, lo, hi):
        """(guest_addr, length) committed, readable runs between guest lo..hi, VirtualQueryEx."""
        info = self.MemoryBasicInformation()
        addr = self.membase + lo
        end = self.membase + hi
        while addr < end:
            if not self.kernel32.VirtualQueryEx(
                    self.handle, ctypes.c_uint64(addr), ctypes.byref(info),
                    ctypes.sizeof(info)):
                break
            run_end = min(end, info.BaseAddress + info.RegionSize)
            if (info.State == self.MEM_COMMIT and
                    not info.Protect & (self.PAGE_NOACCESS | self.PAGE_GUARD)):
                yield addr - self.membase, run_end - addr
            addr = run_end


def find_reader():
    """The best reader for the CURRENT situation, plus a description saying exactly which run
    and which access path answered — the staleness rule applies doubly to post-mortem reads.

    Returns (reader, description, error): reader None means no memory is reachable and error
    says why; description["source"] is "shm-file" or "process", description["state"] is
    "live" (pid up) or "post-mortem" (Linux leak).
    """
    info, error = handshake_info()

    if sys.platform != "win32":
        candidates = []
        if info is not None and info.get("arena"):
            candidates.append(("/dev/shm/" + str(info["arena"]),
                               "live" if info.get("alive") else "post-mortem", info))
        if info is not None and info.get("alive") and not info.get("arena"):
            # A build predating the handshake field: the exact name is in the process's maps.
            try:
                with open(f"/proc/{info['pid']}/maps") as maps:
                    for line in maps:
                        if "xenia_memory_" in line:
                            name = line.rsplit("/", 1)[-1].split()[0].strip()
                            candidates.append(("/dev/shm/" + name, "live", info))
                            break
            except OSError:
                pass
        if not candidates:
            # Post-mortem of last resort: the newest leaked arena. Say so loudly.
            leaked = sorted(glob.glob("/dev/shm/xenia_memory_*"), key=os.path.getmtime)
            if leaked:
                candidates.append((leaked[-1], "post-mortem", None))

        for path, state, handshake in candidates:
            if not os.path.exists(path):
                continue
            try:
                reader = ArenaFileReader(path)
            except OSError:
                continue
            description = {
                "source": "shm-file",
                "path": path,
                "state": state,
                "resident_bytes": os.stat(path).st_blocks * 512,
            }
            if handshake is not None:
                description["pid"] = handshake.get("pid")
                description["boot"] = handshake.get("boot")
            if state == "post-mortem":
                description["warning"] = (
                    "reading a DEAD run's leaked arena — values are from whenever it died, "
                    "and conclusions from it do not apply to any current run"
                )
            return reader, description, None

        return None, None, error or (
            "no guest arena is reachable: no handshake arena, no live pid to inspect, "
            "and no leaked xenia_memory_* in /dev/shm"
        )

    # Windows.
    if info is None:
        return None, None, error
    if not info.get("alive"):
        return None, None, (
            f"the game (pid {info.get('pid')}) is gone, and Windows keeps no post-mortem "
            "arena — the mapping died with the process"
        )
    if not info.get("arena") or not info.get("membase"):
        return None, None, (
            "the handshake has no arena/membase — this build predates them; rebuild, or "
            "the guest is not up yet"
        )
    try:
        reader = ProcessMemoryReader(info["pid"], int(info["membase"]))
    except OSError as failure:
        return None, None, str(failure)
    return reader, {
        "source": "process",
        "pid": info.get("pid"),
        "boot": info.get("boot"),
        "state": "live",
    }, None
