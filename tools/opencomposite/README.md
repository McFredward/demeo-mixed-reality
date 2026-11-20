# Game-local OpenXR adapter

This is OpenComposite (GPL-3.0), pinned to
[`cff07db75c4823afe93ed7027b03d5f7bc86f164`](https://gitlab.com/znixian/OpenOVR/-/tree/cff07db75c4823afe93ed7027b03d5f7bc86f164),
with the Demeo Mixed Reality alpha-control extension. It translates Demeo and
Demeo Battles' OpenVR calls into OpenXR. Battlemarked already uses OpenXR.

The installer places it only in the selected game's native plugin directory
and saves the original API as `openvr_api.dmr-original.dll`. It changes no
system-wide XR setting, SteamVR/Frame binary, service or another game's files.
`Backend=LegacyCompanion` (or `NativePassthrough=false`) at **game startup**
forwards calls to that original API. Switching transports requires restarting
the game. Passthrough itself can toggle during play.

`opencomposite-source.zip` contains the full corresponding upstream sources,
pinned dependencies, original licences and these modifications, including the
build script and DLL smoke fixtures. `build.json` records the exact binary,
source archive and build inputs. No proprietary game assembly is included.

The source ZIP is already patched and can also be built directly after extraction
in a Visual Studio developer shell:

```powershell
cmake -S . -B build -A x64 -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded -DOC_VERSION=DemeoMR-OpenXR-1-cff07db75c48
cmake --build build --config Release --target OCOVR
```

Developers: build on Windows with Visual Studio C++, ATL, Windows SDK, CMake,
Python and Git using `scripts/build-opencomposite.py` in the Demeo Mixed Reality
repository. The build requires a fresh `.build/opencomposite-source` directory.
The adapter uses D3D11 and a static MSVC runtime. Run
`python scripts/check-opencomposite.py` to verify a supplied artifact against
its build inputs. Detailed design and test boundaries are in
`docs/development/OPENCOMPOSITE.md` in the mod repository.

Upstream project: https://gitlab.com/znixian/OpenOVR
