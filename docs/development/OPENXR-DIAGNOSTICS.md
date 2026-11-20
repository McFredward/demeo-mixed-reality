# OpenXR edges and additive-light diagnosis

The first Battles headset report on the new native path describes green edges
and weaker light shafts than the legacy companion. This is an unresolved visual
regression, not evidence that native OpenXR inherently requires colour keying.
Normal builds and ABI tests do not establish optical equivalence on the headset.

## Venture comparison

Read-only comparison against public Venture main commit
[`087ea40470e138607e4f2ea690eb93cb9f4130ba`](https://github.com/Villagers654/Venture/tree/087ea40470e138607e4f2ea690eb93cb9f4130ba):

* [`passthrough.cpp`](https://github.com/Villagers654/Venture/blob/087ea40470e138607e4f2ea690eb93cb9f4130ba/src/runtime/hle/openxr/passthrough.cpp)
  selects the host's advertised alpha blend mode. Native blending and the
  explicitly selected colour-key path are separate.
* [`openxr.cpp`](https://github.com/Villagers654/Venture/blob/087ea40470e138607e4f2ea690eb93cb9f4130ba/src/runtime/hle/openxr/openxr.cpp)
  preserves the guest's layer flags unless an underlay conversion needs to
  incorporate opacity/keying. A converted straight-alpha source is premultiplied
  once and its unpremultiplied flag removed, avoiding a second multiplication.
* [`color.frag`](https://github.com/Villagers654/Venture/blob/087ea40470e138607e4f2ea690eb93cb9f4130ba/src/runtime/xr/shaders/color.frag)
  decodes/encodes sRGB through the image views, preserves premultiplied RGB, and
  modifies RGB by alpha only for a declared straight-alpha source. Reconstruction
  opacity changes coverage, not the already rendered light contribution.

Our translated projection already requests SOURCE_ALPHA without UNPREMULTIPLIED,
copies RGB/alpha rather than multiplying RGB again, and does not request colour
keying. There is no justified flag flip to copy from Venture at this point.
The important remaining differences are the actual game's output/shader alpha,
OpenVR source colour-space declaration, D3D11/MSAA/copy path, and the installed
SteamVR/Steam Link processing. Venture's correct result does not prove that
Battles produces the same input pixels. No Venture checkout was modified.

## Opt-in Battles/Demeo test

Update the mod **and** game-local adapter with `scripts/install.ps1`, close the
game, and edit `BepInEx/config/DemeoMixedReality.cfg` (MelonLoader: the mod INI):

```ini
[STEAMLINK]
NativePassthrough=true
Backend=Auto

[DIAGNOSTICS]
OpenXRAlpha=true
```

Restart the game: the native adapter reads this option at startup. This diagnostic
is only implemented in the Demeo/Battles adapter, not Battlemarked's Unity OpenXR
provider or original-OpenVR LegacyCompanion. Leave it off for normal gameplay.
It does not change any SteamVR setting, companion, device, shader or game material.

Once native alpha has actually been accepted, a head-relative six-cell strip
appears for 120 seconds of monotonic time per eye compositor. In ordinary upright
submissions it sits below the centre. Left to right:

| Cell | Exact linear-light contribution E and coverage A | Expected headset appearance |
|---|---|---|
| 0 | RGB=0, A=0 | Real room only |
| 1 | RGB=.25, A=1 | Opaque grey, hides the room |
| 2 | RGB=(0,1,0), A=1 | Opaque green, hides the room |
| 3 | RGB=.5, A=.5 | White with half room visibility |
| 4 | RGB=.25, A=0 | Added light over the room, not an empty cell |
| 5 | RGB=.25*A, A ramps 0 to 1 | Smooth grey coverage edge without green |

RGB is encoded to match the actual OpenXR view (sRGB or UNORM); coverage is never
gamma-encoded. Equal RGB in cells 1 and 4 deliberately has different coverage.
The strip bypasses Unity's materials, post-processing and the OpenComposite source
copy. A faulty strip therefore localizes the issue downstream of those stages,
assuming its logged synthetic pixel checks pass. It does not alone distinguish
SteamVR composition from encoding, decoding or final Frame composition.
Cell 4 specifically tests the additive-emission behaviour needed to match our
companion. Loss of this cell alone is not proof of an incorrect OpenXR flag or a
runtime conformance bug: it can identify a limitation in preserving zero-coverage
emission through that path. Keep it distinct from the ordinary opacity controls.

Keep the same scene, view and bitrate for the native/legacy scene comparison.
Record both eyes if possible. Check the PC SteamVR Camera settings for a separately
enabled colour-key/Room View setting, and record the existing choice before a
reversible manual comparison. Do not assume that changing Protect/Key Content
necessarily disables all key processing. Do not remove shared companion patches
automatically: that would affect other working games. Their presence is a separate
test variable and must be recorded.

Collect:

* `%LOCALAPPDATA%/OpenComposite/logs/opencomposite.log` immediately after the test
  (fallback: `opencomposite.log` in the game's working directory). The next game
  launch can overwrite it.
* The mod's BepInEx `LogOutput.log` or MelonLoader log, plus `vrserver.txt` and
  `vrcompositor.txt` for the same run and PC/Frame versions.
* Whether opaque green stays opaque, whether cell 4 emits light, whether cell 5
  has green edges, and whether the same defects affect the real game objects.

Set `OpenXRAlpha=false` and restart afterwards. The strip removes itself after
120 seconds even if the option is left on; the next game launch starts a new test.

## Measurement seam and limits

The production D3D11 compositor calls the probe only for an explicit eye and a
current SteamVR session with requested **and previously accepted** alpha blend.
It runs after the game eye copy, before releasing the acquired XR swapchain image.
No reads/writes occur after release. It touches no game-owned source texture and
changes no shader, render target, viewport, sampler or blend state.

Every five seconds, at most 24 snapshots per eye compositor, it copies four
32x32 game regions **before** strip injection, then six 32x32 control regions
**after** injection. A staging texture and EVENT query retain their own snapshot.
One DONOTFLUSH query poll and one DO_NOT_WAIT Map are attempted per later call;
there is no Flush, blocking Map, wait loop or sleep in the production probe.
Outstanding work has a 600-poll ceiling. Size/format changes discard the old
pending snapshot, rather than relabel it using a new layout. Optional failures
stop the probe, leaving normal XR image release/submission running.

Logs carry source storage/MSAA/array/colour-space metadata, actual XR view format,
generation/frame/eye, per-channel min/mean/max, green-excess counts, and exact
synthetic mismatch counts. These are byte-domain summaries, not exported images,
optical measurements or colour-key classifications. Sparse game regions cannot
prove that no fringe exists elsewhere; a green-excess count may be genuine green
content. `synthetic_mismatch=0` establishes only that known RGBA bytes survived
the diagnostic copy/readback seam, not that Steam Link displayed them correctly.

Windows build validation runs the real production probe on D3D11 WARP without
SteamVR or game startup. It checks all four RGBA/BGRA UNORM/sRGB formats, untouched
source/outside pixels, actual asynchronous snapshots and automatic expiry.
Portable checks cover the encoding, additive A0 control, statistics and inactive/
lost-session refusal. These software tests do not replace the headset comparison.
