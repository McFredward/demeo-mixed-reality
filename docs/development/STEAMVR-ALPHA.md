# Built-in SteamVR OpenXR alpha blending

## Scope and selection

Valve's [SteamOS 0.5.8 announcement](https://steamcommunity.com/app/4165890/announcements/detail/527161296156696117?l=english)
adds `XR_ENVIRONMENT_BLEND_MODE_ALPHA_BLEND` for PC-streamed OpenXR games.
It requires the PC SteamVR beta as well as the Frame beta. This is distinct
from chroma key and from our companion's separately transported coverage masks.

`[STEAMLINK] NativePassthrough=true` still requires confirmed Frame/Steam Link
identity and the game's own SteamVR transport. The new `Backend` setting is:

| Value | Selection |
|---|---|
| `Auto` (default) | Native OpenXR: Unity OpenXR in Battlemarked, bundled game-local OpenComposite in Demeo/Battles. No implicit companion fallback. |
| `SteamVR` | Require the same native OpenXR alpha path explicitly. |
| `LegacyCompanion` | Original PC/Frame companion negotiation and submission adapters. |

Battlemarked already runs OpenXR. Demeo and Demeo Battles retain their Unity
OpenVR loader but their API calls are translated into OpenXR by the bundled
OpenComposite adapter. They submit genuine OpenXR projection layers to SteamVR;
there is no private Steam Link mask stream. See
[OpenComposite integration](OPENCOMPOSITE.md) for installation, source, ABI and tests.
All six game/loader builds include the selector. Existing config opt-outs stay
off; missing `Backend` entries default to Auto and are saved in BepInEx configs.
Legacy INI import includes the selector and still runs only on first creation.

## Existing instance, capability and ownership

`SteamVrAlpha.Initialize` resolves the optional Unity OpenXR feature API and
attaches a Harmony prefix to `OpenXRLoaderBase.ReceiveNativeEvent` during mod
initialization. It records the actual instance, system and session handles by
enum **name**, validating the API shape instead of pinning private ordinals or
binary offsets. The original callback always runs. Failure leaves the legacy
backend available. A mod loaded after XR startup waits for fresh callbacks;
restart the game rather than guessing handles or creating another XR instance.

Once that session has begun, the controller resolves
`xrEnumerateEnvironmentBlendModes` using Unity's `xrGetInstanceProcAddr`, querying
the actual instance/system for `PRIMARY_STEREO` (2). Count/data calls require
successful results, nonzero bounded counts (maximum 16), and valid output sizes.
The result is cached for that session generation. Failed queries and absent
alpha support both refuse transparent output, with separate diagnostic reasons.
Runtime version strings do not substitute for the capability query.

The adapter uses Unity's existing `OpenXRFeature.SetEnvironmentBlendMode` and
`GetEnvironmentBlendMode` methods through checked reflection, compatible with
the supplied generated IL2CPP API without adding game code to our distribution.
See [Unity's feature API](https://docs.unity3d.com/Packages/com.unity.xr.openxr@1.16/api/UnityEngine.XR.OpenXR.Features.OpenXRFeature.html).
Unity queues the mode request: the setter returning is **not** proof of active
alpha composition. Request once; allow an alpha-only preparation pass with an
opaque black camera clear before that request reaches frame submission. This
avoids presenting unprepared game alpha in the first accepted frame. Clear the
camera transparently only after a subsequent getter reports AlphaBlend (3).
A three-second deadline with at least three
pending observations cancels an unaccepted request rather than writing forever.

The lease saves the original mode, restores only its owned selection, cancels
pending requests on opt-out, and handles off/on before queued restoration has
been consumed. Preexisting external AlphaBlend is used without claiming a write.
External mode changes revoke admission without overwriting the new owner.
Normal session end restores before the original lifecycle callback runs; loss
or destruction invalidates handles and never writes through them. Session
generation changes immediately invalidate source permission and recheck support.

## Rendering and legacy boundary

`NativePassthrough` has separate permission checks for direct SteamVR alpha and
the legacy negotiated source grant. The direct path never fabricates a companion
snapshot or declares native mask transport operational. It stops the old source
pipe and bypasses the OpenVR array Submit/filter adapter and Battlemarked's
legacy forced-MultiPass lease. Unity/SteamVR retain their actual stereo layout.

Both backends share current-camera/frame validation, SDR RGBA target validation,
opaque-depth alpha repair, alpha-preserving HDR intermediates, post-processing
bypass, loading/fade suppression and restoration. The procedural URP repair uses
Unity's XR instance multiplier for array eyes; it does not add a second instance.
Opaque objects keep coverage 1; transparent materials retain fractional alpha.
No new camera, colour-derived mask, global passthrough toggle, foveation override
or game/controller recenter behavior is introduced by this backend.

Old companion code and its source/frame/eye proofs remain intact. The installer
adds only game-local OpenComposite adapters for the first two games. It does
not alter PC/Frame patch binaries, install or uninstall a companion, restart
SteamVR, modify a headset, or touch GloomhavenVR. An existing patched runtime is
not claimed as a clean validation of Valve's unmodified transport.

## Validation and remaining headset checks

`scripts/check-integration.py` covers all three games with both loaders. It
exercises queued activation, timeout, generation loss, external ownership and
rapid off/on, plus the real shared camera/URP preparation without a companion
grant. It checks that the native route retains array eyes and still records the
opaque-depth repair. Existing legacy rendering tests remain enabled.

`tests/SteamVrAlphaInteropChecks.csproj` executes the actual reflection adapter
and real Harmony patch against a lifecycle fixture with deliberately different
enum ordinals and native unmanaged-callable OpenXR function pointers. It checks
exact instance/system/view arguments, count/data ABI, acceptance, end/loss,
opaque-only re-enumeration, original-callback forwarding and stale-handle refusal.
This is an ABI/lifecycle test, **not** a headset composition or IL2CPP detour test.
`scripts/check-config.py` uses real BepInEx 5/6 configs for defaults, upgrades,
preserved choices, migration and live reload.

Before claiming the new path is headset-tested:

1. Use unmodified PC/Frame runtimes supporting the advertised beta feature and
   set SteamVR as OpenXR runtime. Start Battlemarked with Backend=Auto.
2. Retain `SteamVR OpenXR alpha capability: available` and
   `Native SteamVR OpenXR alpha blending active` logs. There should be no companion
   source permission requirement or legacy MultiPass setting change.
3. Check intro, initial loading text, menu, scenario loading and gameplay in
   both eyes. Check solid geometry, translucent effects, fades and Steam overlay.
4. Toggle NativePassthrough off/on (including quickly), reconnect/restart XR and
   verify restoration. Compare Backend=LegacyCompanion separately.
5. Verify LegacyCompanion with old runtimes separately and check Virtual Desktop
   colour replacement with NativePassthrough=false. Changing the game-local
   adapter's startup transport requires a game restart.

No visual success, bandwidth saving, or clean-runtime headset result is claimed
from automated tests alone.
