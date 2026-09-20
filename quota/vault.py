"""Windows DPAPI CurrentUser encrypted app state outside the repository."""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path


class Blob(ctypes.Structure):
    _fields_ = [('size', wintypes.DWORD), ('data', ctypes.POINTER(ctypes.c_ubyte))]


def crypt(data, decrypt=False):
    if os.name != 'nt':
        raise RuntimeError('Windows DPAPI is required')
    buffer = ctypes.create_string_buffer(data)
    source = Blob(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
    target = Blob()
    api = ctypes.WinDLL('crypt32', use_last_error=True)
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    kernel.LocalFree.restype = ctypes.c_void_p
    fn = api.CryptUnprotectData if decrypt else api.CryptProtectData
    # CRYPTPROTECT_UI_FORBIDDEN, never machine-wide protection.
    if not fn(ctypes.byref(source), None, None, None, None, 1, ctypes.byref(target)):
        raise RuntimeError('Windows encryption failed')
    try:
        return ctypes.string_at(target.data, target.size)
    finally:
        kernel.LocalFree(target.data)


class Vault:
    def __init__(self, path=None):
        self.path = Path(path) if path else Path(os.environ['LOCALAPPDATA']) / 'QuotaDashboard' / 'snapshot.dpapi'

    def load(self):
        if not self.path.exists():
            return {}
        return json.loads(crypt(self.path.read_bytes(), True))

    def save(self, value):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temp = self.path.with_suffix('.tmp')
        temp.write_bytes(crypt(json.dumps(value, allow_nan=False).encode()))
        os.replace(temp, self.path)
