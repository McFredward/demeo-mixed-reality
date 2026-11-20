#!/usr/bin/env python3
"""Verify shipped adapter provenance, exact source inputs and Windows API surface."""
import hashlib
import json
from pathlib import Path
import struct
import zipfile

ROOT = Path(__file__).resolve().parent.parent


class PE:
    def __init__(self, path):
        self.data = path.read_bytes()
        pe = self.u32(60)
        if self.data[:2] != b'MZ' or self.data[pe:pe+4] != b'PE\0\0' or self.u16(pe+4) != 0x8664 or self.u16(pe+24) != 0x20b:
            raise ValueError('Expected Windows x64 PE: ' + str(path))
        if not self.u16(pe+22) & 0x2000: raise ValueError('Adapter must be a DLL')
        self.directories = pe + 24 + 112
        start = pe + 24 + self.u16(pe+20)
        self.sections = [struct.unpack_from('<8sIIII',self.data,start+40*i) for i in range(self.u16(pe+6))]
    def u16(self, offset): return struct.unpack_from('<H',self.data,offset)[0]
    def u32(self, offset): return struct.unpack_from('<I',self.data,offset)[0]
    def rva(self, value):
        for _, size, rva, rawsize, offset in self.sections:
            if rva <= value < rva + max(size,rawsize):
                result = offset+value-rva
                if result >= len(self.data): break
                return result
        raise ValueError('Invalid PE RVA')
    def string(self, value):
        offset = self.rva(value)
        return self.data[offset:self.data.index(b'\0',offset)].decode('ascii')
    def exports(self):
        export = self.rva(self.u32(self.directories))
        count = self.u32(export+24)
        if count > 4096: raise ValueError('Invalid export count')
        names = self.rva(self.u32(export+32))
        return {self.string(self.u32(names+4*i)) for i in range(count)}
    def imports(self):
        value = self.u32(self.directories+8)
        if not value: return set()
        descriptor = self.rva(value)
        names = set()
        for i in range(256):
            cursor = descriptor+20*i
            thunk, _, _, name, fallback = struct.unpack_from('<IIIII',self.data,cursor)
            if not name: return names
            names.add(self.string(name).lower())
        raise ValueError('Invalid import count')


def main():
    directory = ROOT / 'assets/opencomposite'
    receipt = json.loads((directory / 'build.json').read_text())
    binary = directory / 'opencomposite.bin'
    archive = directory / 'opencomposite-source.zip'
    sha = lambda data: hashlib.sha256(data).hexdigest()
    assert receipt['schema'] == 'demeo-opencomposite-build-v1'
    assert receipt['upstream'] == 'cff07db75c4823afe93ed7027b03d5f7bc86f164'
    assert receipt['dll_smoke_tested'] is True and receipt['headset_tested'] is False
    assert receipt.get('diagnostic_warp_tested') is True, 'Missing Windows software-GPU diagnostic validation'
    assert sha(binary.read_bytes()) == receipt['binary_sha256'], 'Native binary hash changed'
    assert sha(archive.read_bytes()) == receipt['source_sha256'], 'Corresponding source archive hash changed'
    required = {'tools/opencomposite/'+name for name in ('apply.py','dmr_bridge.cpp','dmr_bridge.h','dmr_state.hpp','dmr_smoke.cpp','dmr_fake_original.cpp',
                                                       'dmr_diagnostics.cpp','dmr_diagnostics.h','dmr_diagnostic_pattern.hpp','dmr_diagnostics_checks.cpp')}
    required.add('scripts/build-opencomposite.py')
    assert required <= receipt['inputs'].keys(), 'Missing native build input proofs'
    with zipfile.ZipFile(archive) as source:
        assert 'LICENSE.txt' in source.namelist() and 'OpenOVR/dmr_bridge.cpp' in source.namelist()
        for relative, digest in receipt['inputs'].items():
            path = (ROOT / relative).resolve()
            assert path.is_relative_to(ROOT) and path.is_file(), 'Unexpected source input path'
            archived = source.read('DemeoMR/' + path.name)
            assert sha(archived) == digest, 'Build input missing from corresponding source: ' + relative
            # Git on Windows may check text out as CRLF. Verify the exact build
            # bytes above, then require identical normalized current source.
            assert archived.replace(b'\r\n', b'\n') == path.read_bytes().replace(b'\r\n', b'\n'), 'Native adapter needs rebuilding: ' + relative
    pe = PE(binary)
    required_exports = {'DMR_GetState','DMR_SetBlendMode','VR_GetGenericInterface','VRHeadsetView','VR_ShutdownInternal',
                        'VR_InitInternal2','VR_GetInitToken','VR_IsInterfaceVersionValid','VR_GetVRInitErrorAsSymbol','VR_GetRuntimePath'}
    assert required_exports <= pe.exports(), 'Missing mod API or actual XRSDKOpenVR import'
    assert 'vulkan-1.dll' not in pe.imports(), 'D3D11 adapter must not require a Vulkan installation'
    assert not any(name.startswith(('vcruntime','msvcp')) for name in pe.imports()), 'Adapter must use the static MSVC runtime'
    print('OpenComposite adapter: matching source/receipt, Windows x64 exports, static CRT and D3D11-only imports verified')


if __name__ == '__main__': main()
