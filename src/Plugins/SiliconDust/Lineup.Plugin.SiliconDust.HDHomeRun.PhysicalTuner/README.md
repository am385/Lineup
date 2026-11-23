# Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner

The physical HDHomeRun adapter and EXTEND device-side transcoding.
See the [Device project](../Lineup.Plugin.SiliconDust.HDHomeRun.Device/README.md#receiver-specific-status-and-deadlines) for native protocol diagnostics.

- [HDHomeRun EXTEND device-side transcoding](#hdhomerun-extend-device-side-transcoding)

## HDHomeRun EXTEND device-side transcoding

Each HDHomeRun physical device has an operator-controlled **HDHomeRun EXTEND device transcoding** profile.
**Device default** adds no `transcode` parameter, preserving existing behavior and the device's own defaults; it does not
force an EXTEND configured for transcoding back to native video. Explicit profiles are allowed only for discovered
EXTEND model families (`HDTC-2US`, `HDTC-2US-M`, `HDTC-2US-L`), never inferred from an editable friendly name.

| Profile | Device behavior |
|---|---|
| `heavy` | High-bandwidth H.264; can retain the original resolution, frame rate and interlacing |
| `mobile` | H.264 up to 720p30 |
| `internet540`, `internet480`, `internet360`, `internet240` | Progressively lower-resolution, bandwidth-oriented H.264 |

The selected profile is safely merged into both ordinary and diagnostic lineup HTTP stream requests. Existing query
parameters are retained; explicit profiles replace every existing `transcode` entry with one selected value (including
case-insensitive or percent-encoded parameter names). Device default leaves the original URL unchanged. Unsupported models/profiles fail
explicitly rather than silently ignoring the setting; HDHomeRun error `802` is reported as an unknown transcode profile.
Adapter descriptors, configuration and model-based capability diagnostics expose the choice.

EXTEND has two tuner/transcoder paths. FLEX, CONNECT and PRIME are not interchangeable general-purpose transcoders.
**EXTEND does not transcode audio**, so it cannot solve AC-4 compatibility; downstream Lineup audio conversion may still
be required. Lineup keeps its normal metadata/deinterlace policies for the returned stream instead of assuming every profile
is progressive. Closing and disposing the upstream HTTP stream releases the tuner; device-side transcoding does not create
a Lineup-owned GPU process. Client route query parameters do not override the configured device profile.
See SiliconDust's [HTTP development documentation](https://info.hdhomerun.com/info/http_development).

## Related documentation

[Project overview](../../../../README.md) | [Contributor standards](../../../../CONTRIBUTING.md)
