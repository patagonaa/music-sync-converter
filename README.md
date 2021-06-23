# music-sync-converter
Sync music for use on flash drives in car radios and such. Convert unsupported files by extension, codec and profile.

## Usage:
`.\MusicSyncConverter.exe config.json`

### Example config:
- Sync `Z:\Audio` to `E:\Audio`
- Convert all files that are not MP3, WMA or AAC-LC to AAC-LC 192kbit/s
- Exclude `Z:\Audio\Webradio` and `Z:\Audio\Music\Albums\Nickelback`

```js
{
    "DeviceConfig": {
        "SupportedFormats": [
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
            },
            {
                "Extension": ".m4a",
                "Codec": "aac", // codec name as reported by ffprobe
                "Profile": "LC" // codec profile as reported by ffprobe
            }
        ],
        "FallbackFormat": {
            "Extension": ".m4a",
            "EncoderCodec": "aac", // as required for ffmpeg (-c:a aac)
            "EncoderProfile": "aac_low", // as required for ffmpeg (-profile:a aac_low), may be omitted
            "Bitrate": 192 // kbit/s
        }
    },
    "SourceDir": "Z:\\Audio\\",
    "TargetDir": "E:\\Audio\\",
    "Exclude": [
        "Webradio",
        "Music\\Albums\\Nickelback"
    ]
}
```