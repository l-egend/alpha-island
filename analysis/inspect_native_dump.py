"""Read an existing local crash dump through Windows' debugger engine."""
import ctypes as c
import sys
import uuid
from pathlib import Path


class Guid(c.Structure):
    _fields_ = [("data1", c.c_uint32), ("data2", c.c_uint16), ("data3", c.c_uint16), ("data4", c.c_ubyte * 8)]


def guid(value):
    return Guid.from_buffer_copy(uuid.UUID(value).bytes_le)


def call(owner, index, types, *args):
    table = c.cast(owner, c.POINTER(c.POINTER(c.c_void_p))).contents
    method = c.WINFUNCTYPE(c.c_int32, c.c_void_p, *types)(table[index])
    result = method(owner, *args)
    if result < 0:
        raise RuntimeError(f"Debugger method {index}: HRESULT {result & 0xffffffff:08x}")
    return result


dump = Path(sys.argv[1]).resolve()
log = Path(sys.argv[2]).resolve()
log.parent.mkdir(parents=True, exist_ok=True)
engine = c.WinDLL("dbgeng.dll")
engine.DebugCreate.argtypes = [c.POINTER(Guid), c.POINTER(c.c_void_p)]
engine.DebugCreate.restype = c.c_int32
client = c.c_void_p()
client_id = guid("27fe5639-8407-4f47-8364-ee118fb08ac8")
result = engine.DebugCreate(c.byref(client_id), c.byref(client))
if result < 0:
    raise RuntimeError(f"DebugCreate: {result & 0xffffffff:08x}")
control = c.c_void_p()
control_id = guid("5182e668-105e-416e-ad92-24ef800424ba")
call(client, 0, [c.POINTER(Guid), c.POINTER(c.c_void_p)], c.byref(control_id), c.byref(control))
call(control, 8, [c.c_char_p, c.c_int32], str(log).encode("mbcs"), 0)
try:
    for command in (b".sympath", b".symopt+0x10"):
        call(control, 66, [c.c_uint32, c.c_char_p, c.c_uint32], 4, command, 0)
    call(client, 19, [c.c_char_p], str(dump).encode("mbcs"))
    call(control, 93, [c.c_uint32, c.c_uint32], 0, 10000)
    commands = sys.argv[3] if len(sys.argv) > 3 else ".ecxr; kv 40; lm; !analyze -v"
    for command in commands.split(";"):
        call(control, 66, [c.c_uint32, c.c_char_p, c.c_uint32], 4, command.strip().encode("mbcs"), 0)
finally:
    call(control, 9, [])
    call(control, 2, [])
    call(client, 2, [])
print(log)
