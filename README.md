# music-sync-converter
Sync music for use on flash drives in car radios and such. Detect and convert unsupported files by extension, codec and profile.
Works on Windows and Linux, macOS is untested.

## Usage:
`.\MusicSyncConverter.exe config.json`

### Example config:
- Sync `Z:\Audio` to `E:\Audio`
- Copy/Remux all MP3, WMA and AAC-LC files
- Convert all unsupported files (fall back to AAC-LC 192kbit/s)
- Replace unsupported characters (in directory and file names, and tag values)
- Convert album covers of unsupported files to jpeg with 320x320 px max (while retaining aspect ratio)
- Exclude `Z:\Audio\Webradio` and `Z:\Audio\Music\Albums\Nickelback`
- Change every first character of file/dir names to uppercase so things that sort case-sensitive work properly
- Reorder file table (required if the target device doesn't sort files and/or folders by itself and instead uses the FAT order)

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
            "Codec": "aac", // as required by ffmpeg (-c:a aac)
            "Profile": "aac_low", // as required by ffmpeg (-profile:a aac_low), may be omitted
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
            ],
            "NormalizeCase": true // change every first character of file/dir names to uppercase
        },
        "FatSortMode": "Folders", // Valid Values are "None", "Files", "Folders", "FilesAndFolders"
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
    "WorkersRead": 8, // max number of threads to use for reading files
    "WorkersConvert": 8, // max number of threads to use for converting files
    "WorkersWrite": 1 // max number of threads to use for writing (for slow devices like HDDs, SD cards or flash drives, 1 is usually best)
}
```

You can also split the configuration file into multiple files and supply multiple config files as arguments, which might be useful if converting for different end devices but with the same directory settings.

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

## TODO?
- [x] make basic functionality work
- [x] add album art support
- [x] add character limitation support
- [x] add support for single-threaded writing for slow output devices
- [x] replace unsupported characters in tags
- [x] test on linux
- [x] split SyncService into more manageable parts