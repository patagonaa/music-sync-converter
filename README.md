# music-sync-converter
Sync music for use on flash drives in car radios and such. Detect and convert unsupported files by extension, codec and profile.

_Should_ work on Linux and macOS as well, but is untested.

## Usage:
`.\MusicSyncConverter.exe config.json`

### Example config:
- Sync `Z:\Audio` to `E:\Audio`
- Copy all MP3, WMA and AAC-LC files
- Convert all unsupported files (Fall back to AAC-LC 192kbit/s)
- Exclude `Z:\Audio\Webradio` and `Z:\Audio\Music\Albums\Nickelback`

```js
{
    "DeviceConfig": {
        "SupportedFormats": [
            {
                "Extension": ".m4a",
                "Codec": "aac", // as reported by ffprobe
                "Profile": "LC" // as reported by ffprobe
            },
            {
                "Extension": ".mp3",
                "Codec": "mp3"
            },
            {
                "Extension": ".wma",
                "Codec": "wmav1"
            },
            {
                "Extension": ".wma",
                "Codec": "wmav2"
            }
        ],
        "FallbackFormat": {
            "Extension": ".m4a",
            "EncoderCodec": "aac", // as required by ffmpeg (-c:a aac)
            "EncoderProfile": "aac_low", // as required by ffmpeg (-profile:a aac_low), may be omitted
            "Muxer": "mp4", // as required by ffmpeg (usually, this is the container format)
            "Bitrate": 192 // kbit/s
        }
    },
    "SourceDir": "Z:\\Audio\\",
    "TargetDir": "E:\\Audio\\",
    "Exclude": [
        "Webradio",
        "Music\\Albums\\Nickelback"
    ],
    "WorkersConvert": 8, // max number of threads to use for reading / converting files
    "WorkersWrite": 1 // max number of threads to use for writing (for slow devices like HDDs, SD cards or flash drives, 1 is usually best)
}
```

"as reported by ffprobe" =>
```
Stream #0:0(und): Audio: aac (LC) (mp4a)
                   Codec ^    ^ Profile

Stream #0:0(und): Audio: aac (HE-AAC)
                   Codec ^    ^ Profile

Stream #0:0: Audio: mp3
              Codec ^
```

"as required for ffmpeg" =>
```
ffmpeg -i input.mp3 -c:a aac -profile:a aac_low
           Encoder/Codec ^              ^ Profile (if applicable)
```