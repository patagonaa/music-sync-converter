# music-sync-converter
Sync music for use on flash drives in car radios and such. Detect and convert unsupported files by extension, codec and profile.

_Should_ work on Linux and macOS as well, but is untested.

## Usage:
`.\MusicSyncConverter.exe config.json`

### Example config:
- Sync `Z:\Audio` to `E:\Audio`
- Copy all MP3, WMA and AAC-LC files
- Replace unsupported characters
- Convert all unsupported files (Fall back to AAC-LC 192kbit/s)
- Convert album covers to jpeg with 320x320 px max (while retaining aspect ratio)
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
            "Muxer": "ipod", // as required by ffmpeg (usually, this is the container format)
            "AdditionalFlags": "-movflags faststart", // additional arguments to pass to ffmpeg
            "Bitrate": 192, // kbit/s
            "CoverCodec": "mjpeg", // format to use for album covers ("mjpeg" = jpg, "png" = png, null = remove album convers)
            "MaxCoverSize": 320 // maximum size of album covers in either axis (null = keep original size)
        },
        "CharacterLimitations": { // omit this if your device supports unicode
            "SupportedChars": "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ+-_ (),'[]!&", // all natively supported characters
            "Replacements": [ // characters that should be replaced by different characters
                {
                    "Char": "ä",
                    "Replacement": "ae"
                },
                {
                    "Char": "Ä",
                    "Replacement": "Ae"
                },
                {
                    "Char": "ö",
                    "Replacement": "oe"
                },
                {
                    "Char": "Ö",
                    "Replacement": "Oe"
                },
                {
                    "Char": "ü",
                    "Replacement": "ue"
                },
                {
                    "Char": "Ü",
                    "Replacement": "Ue"
                },
                {
                    "Char": "ß",
                    "Replacement": "ss"
                }
            ]
        }
    },
    "SourceDir": "Z:\\Audio\\",
    "SourceExtensions": [ // file extensions to check (can be omitted, default: mp3, ogg, m4a, flac, opus, wma, wav)
        ".mp3",
        ".ogg",
        ".m4a",
        ".flac",
        ".opus",
        ".wma",
        ".wav"
    ],
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