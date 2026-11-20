# Demeo / Battles: OpenVR to native OpenXR passthrough

## Architecture

The supplied Demeo and Demeo Battles builds contain Unity's OpenVR provider and
SteamVR action bindings, but no usable Unity OpenXR loader. Replacing only a
managed loader setting cannot turn them into OpenXR games. We retain that Unity
provider and translate its public OpenVR calls with a game-local OpenComposite
adapter. SteamVR receives real OpenXR projection layers and handles streaming,
foveation, tracking and local passthrough itself. The companion's private mask
codec, wire protocol and Frame changes are absent from this route.

Battlemarked uses its original Unity OpenXR provider and the feature API in
[STEAMVR-ALPHA.md](STEAMVR-ALPHA.md). All three games share the existing mod's
camera selection, current-frame profile checks, opaque alpha prefix, HDR alpha
preservation, transparent effects, fades/loading handling and cleanup. No table,
room, head or controller transform is changed by the new translation adapter.

Upstream [OpenComposite](https://gitlab.com/znixian/OpenOVR) is pinned to
`cff07db75c4823afe93ed7027b03d5f7bc86f164`; `tools/opencomposite/apply.py` applies
bounded, exact-context changes. The upstream version hardcodes opaque frame-end
composition. A stock downloaded DLL therefore does **not** provide our alpha
control contract. Neither a Unity loader name nor an OpenComposite filename is
proof of native alpha support.

## Native control contract

The adapter exports `DMR_GetState(bytes, out state)` and
`DMR_SetBlendMode(generation, mode)`, C calling convention, 64-bit Windows.
The 24-byte state is version1: uint32 version/bytes, uint64 generation,
uint32 flags/mode. Flags distinguish native versus forwarded original OpenVR,
known OpenXR system, real running session, actual SteamVR runtime, enumerated
alpha capability and the last accepted frame-end blend mode.

The adapter enumerates environment blend modes on **its own actual OpenXR
instance/system** at session creation and reads the runtime name using
`xrGetInstanceProperties`. It does not create another instance for the mod's
capability query. Each session/begin/end/loss advances generation. Mod commands
must match that generation. A mode3 request requires a running SteamVR session
and an enumerated AlphaBlend mode; no version string grants capability.

The managed `OpenCompositeAlpha` adapter binds only an already-loaded game
`openvr_api.dll` with these exports and validates state size/version. It never
loads a competing XR implementation. Unity may load the native DLL after the
mod: cold binding retries account for that. `SteamLinkConnectionLifecycle`
distinguishes translated OpenXR from original OpenVR even though Unity's
managed loader class is still named OpenVRLoader. The existing isolated stock
OpenVR reader independently confirms Frame over Steam Link before alpha can be
requested. Finding the DLL is insufficient.
The recognized control module is kept mapped for the game process lifetime so
late managed cleanup cannot call an unmapped function pointer. The original
VR shutdown still retires its OpenXR session and invalidates the generation.

`SteamVrAlpha.Lease` uses the same queued-request/acceptance and ownership rules
for this backend as for Unity OpenXR. An acknowledged setter only queues a
request. The reported active mode changes after successful `xrEndFrame` using
that mode. It is evidence of submission acceptance, not independent proof that
the headset displayed a particular picture.

The depth prefix is prepared before the first queued alpha frame is submitted.
Its camera clear remains opaque black until acceptance; RGB is already black
where the prefix sets background alpha0. This prevents the first alpha frame
from sampling the game's unprepared alpha. Opaque depth becomes alpha1 before
transparent materials draw. Final scene RGB is treated as premultiplied emission:
projection layers use `BLEND_TEXTURE_SOURCE_ALPHA`, without another RGB-by-alpha
multiply or an `UNPREMULTIPLIED_ALPHA` declaration. Additive light can therefore
remain visible even at alpha0.

## Eye and session correctness

OpenComposite copies each current game eye into its own OpenXR swapchain and
uses the original current view poses/FOV. The alpha extension clears its eye
completion mask at each real frame begin and marks an eye only after its copy
returns. Alpha projection layers require both current eyes. A missing eye does
not authorize recycling a previous eye; the scene layer is omitted while the
alpha environment blend remains requested. No image is classified by RGB colour.

Two upstream D3D11 details require corrections for Unity:

- Initial graphics binding handoff creates a new OpenXR session. Begin its frame
  before copying the first eye; the original ordering could mark an uncopied
  initial swapchain image as submitted.
- The MSAA array path resolved every eye to subresource0 but copied the right eye
  from subresource1. Resolve/copy must use the same actual eye subresource, with
  a typed swapchain-compatible resolve format and single-sample quality0.
- Swapchain compatibility must include the source's MSAA sample count and array
  size. Otherwise an intro without MSAA followed by an MSAA menu at the same
  resolution reuses a chain with no resolver textures. Resolver allocation
  failures must also be checked before use.

These changes retain both array eyes and native sampling/colour encoding. The
old provider-binary pin, 2D Submit interception and Submit-scoped filter override
belong only to the explicitly selected legacy companion path. They are not
applied on top of translated OpenXR.

## Installation, legacy and updates

`scripts/install.ps1` preflights the bundled payload and all selected game paths,
builds the mod against local references and installs the game-local adapter for
Demeo/Battles. ZIP users run the bundled `Install-OpenXR.ps1` once after extraction.
Battlemarked does not replace a native API DLL.

`scripts/OpenXR.Install.Helpers.psm1` verifies the native build manifest/hash and
PE architecture, refuses running games, preserves the original API, stages atomic
replacements and keeps an install receipt plus versioned local history. Repeated
installation of the same artifact is a no-op. If Steam updates the game's API,
rerunning installation preserves that new original before reapplying the adapter;
it never promotes an already-owned adapter to the original. Changed backups and
post-preflight target changes are refused. Receipt replacement failure rolls back
the changed DLLs. `Install-OpenXR.ps1 -RestoreOpenVR` restores only an owned target.
No game process, SteamVR process, service or headset is stopped or started.

The native DLL reads the normal BepInEx config once before its first OpenVR call,
falling back to supported legacy INI locations only when no normal config exists.
The install receipt records the selected loader; a MelonLoader installation reads
its Mods INI instead, even when an old BepInEx config is still present.
NativePassthrough=false or Backend=LegacyCompanion forwards public API calls to
the preserved original game DLL. This keeps the old companion and Virtual Desktop
route available. Runtime selection is process-lifetime state: restart the **game**
when switching between translated OpenXR and original OpenVR. Live passthrough
opt-out still cancels alpha immediately; it does not rebuild XR under the game.

Auto defaults to native OpenXR, with no silent companion fallback. An uninstalled
adapter or unsupported runtime produces a specific diagnostic; users can choose
LegacyCompanion explicitly. SteamVR must be selected as the PC OpenXR runtime.
The installer does not rewrite the user's global runtime preference.

## Source, build and validation

For the unresolved native edges/light comparison and its opt-in measurement mode,
see [OpenXR diagnostics](OPENXR-DIAGNOSTICS.md).

`scripts/build-opencomposite.py` builds the pinned upstream and dependencies on
Windows/MSVC, with ATL and the installed Windows SDK. Only D3D11 is enabled;
unused Vulkan blocks are excluded and no Vulkan installation is required. The
MSVC runtime is linked statically. A missing direct chrono include is added for
current MSVC rather than relying on removed transitive headers.

The Windows build loads the actual DLL in native and legacy fixtures. It checks
the Unity provider's imported API surface, state ABI and refusal before a real
session. Legacy forwarding is checked against a distinguishable fake original
API. These tests do not initialize OpenXR, SteamVR or a headset. The produced
binary, full corresponding source ZIP and exact build inputs have hashes in
`assets/opencomposite/build.json`. Source ZIPs include the original upstream
licences, pinned OpenXR SDK/GLM and adapter modifications. No game code is shipped.

`scripts/check-opencomposite.py` checks those receipts against current source,
required exports, architecture, D3D11-only imports and static CRT. Packaging fails
if native source changed without rebuilding the adapter. `OpenCompositeAlphaChecks.cpp`
tests the production session/frame/eye state machine. `OpenXRInstallerChecks.ps1`
tests installation, updates, restore, ownership checks, running-game refusal,
concurrent file changes and rollback. Existing six game/loader integration tests
exercise shared native rendering plus the separate legacy path.

Actual D3D11 resolve pixels, controller bindings/poses through SteamVR, intro,
loading, menu, gameplay, translucent/additive effects, Steam overlay and both-eye
passthrough still require a headset test for **each game**. Automated lifecycle
tests and a successful DLL load are not hardware validation. Gloomhaven and both
companion repositories/binaries are unchanged by this integration.

Coexistence with previously installed companion patches is not yet headset-verified.
Their authenticated ordinary-video and recovery policies can affect presentation;
an active patched runtime is not a clean baseline for Valve's built-in transport.
The game installer deliberately does not remove or change global patches. If a
native-mode acceptance log disagrees with the headset picture, inspect those
sender/receiver logs as well, and preserve any working Gloomhaven setup when
planning a separate clean-runtime comparison.
