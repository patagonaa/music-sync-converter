# music-sync-converter
Sync music to MP3 players, phones, flash drives for car radios and such. Detect and convert unsupported files by extension, codec and profile automatically.
Works on Windows and Linux, macOS is untested.

## Usage:
`.\MusicSyncConverter.exe config.json`

You can also split the configuration file into multiple files and supply multiple config files as arguments, which might be useful if converting for different end devices but with the same directory settings.

### Supported sources / targets
Instead of just syncing from directory to directory you can use some different file providers (or even write your own):
#### Sources
- File system: `file://` (for example `file://C:/Users/me/Music` or `file://~/Music` or `file:///home/me/Music`)
- WebDAV: `http(s)://` (for example `https://user:password@nextcloud.example.com/remote.php/dav/files/me/Music`)
#### Targets
- File system: `file://` (for example `file://F:/Music` or `file:///mnt/usb/Music`)
    - supports the query parameter `?fatSortMode=<mode>` where `<mode>` is `None`, `Folders`, `Files`, `FilesAndFolders` to sort the FAT32 table when directories change. This is useful for devices that don't sort files or directory by name.
- (on Windows) MTP using [WPD](https://docs.microsoft.com/en-us/windows/win32/windows-portable-devices) : `wpd://` (for example `wpd://My Android Phone/disk/Music`)
    - Don't expect this to be rock-solid. It's MTP, what do you expect?

### Formats
Format conversion works by analyzing the source file, comparing the format against the configured supported formats and converting to the configured fallback format if necessary

The supported format includes:
- `Extension`: File extension (required)
- `Codec`: Codec as reported by ffprobe, for example `aac` (required)
- `Profile`: Profile as reported by ffprobe, for example `LC` for AAC-LC
- `MaxChannels`: Max. number of audio channels
- `MaxSampleRateHz`: Max. sample rate in Hz

"as reported by ffprobe" =>
```
Stream #0:0(und): Audio: aac (LC) (mp4a)
                   Codec ^    ^ Profile

Stream #0:0(und): Audio: aac (HE-AAC)
                   Codec ^    ^ Profile

Stream #0:0: Audio: mp3
              Codec ^
```

The fallback format includes:
- `Extension`: File extension (required)
- `Codec`: Codec as required by ffmpeg, for example `libmp3lame`, `libopus` or `aac` (required)
- `Profile`: Profile as required by ffmpeg
- `Channels`: Number of audio channels
- `SampleRateHz`: Sample rate in Hz
- `Muxer`: Muxer, for example `ipod`
- `AdditionalFlags`: Additional parameters to pass to ffmpeg, for example `-movflags faststart`, which is required by a lot of players using the `mp4` or `ipod` muxer
- `Bitrate`: Bitrate in kbit/s
- `CoverCodec`: Format to use for album covers (`mjpeg` = jpg, `png` = png, `null` = remove album covers)
- `MaxCoverSize`: Max. cover size in either axis

"as required for ffmpeg" =>
```
ffmpeg -i input.mp3 -c:a aac -profile:a aac_low
           Encoder/Codec ^              ^ Profile (if applicable)
```

See below for an example config.

### Album covers
If there are files named `cover.png`, `cover.jpg`, `folder.jpg`, in a song's directory, the album cover is added to the song (if the fallback format includes a cover codec)

### Playlists
There are two ways to handle `m3u` / `m3u8` playlists:
#### ResolvePlaylists false (default)
Each playlist is copied to the target directory. All references are updated to point to the correct file if required (e.g. if the file extension changes due to a format conversion).

All songs referenced by the playlist should be included in the sync task or the songs will be excluded from the playlist and a warning will be logged.

#### ResolvePlaylists true
Each playlist is created as a directory containing the referenced songs.
The `EXTINF` name is used as a file name if available (else the source file name is used).
so the playlist `Playlists/Test.m3u8`
with the contents
```m3u
#EXTM3U
#EXTINF:253,An Artist - Song 4
..\Artists\An Artist\An Album\04 Song 4.flac
```
results in the directory `Playlists/Test/` with the file `An Artist - Song 4.flac`

### Example config:
- Sync `Z:\Audio` to `E:\Audio`
- Copy/Remux all MP3, WMA and AAC-LC files
- Convert all unsupported files (fall back to AAC-LC 192kbit/s)
- Replace unsupported characters (in directory and file names, and tag values)
- Convert album covers of unsupported files to jpeg with 320x320 px max (while retaining aspect ratio)
- Exclude `Z:\Audio\Webradio`, `Z:\Audio\Music\Artists\Nickelback` and `Z:\Audio\Music\Artists\**\Instrumentals` (only `*` and `**` are supported)
- Change every first character of file/dir names to uppercase so devices that sort case-sensitive work properly
- Resolve playlists to directories
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
            "CoverCodec": "mjpeg", // format to use for album covers ("mjpeg" = jpg, "png" = png, null = remove album covers)
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
        "ResolvePlaylists": true // convert playlists to directories with the respective files
    },
    "SourceDir": "file://Z:\\Audio\\",
    "SourceExtensions": [ // file extensions to check (can be omitted, default: mp3, ogg, m4a, flac, opus, wma, wav)
        ".mp3",
        ".ogg",
        ".m4a",
        ".flac",
        ".opus",
        ".wma",
        ".wav"
    ],
    "TargetDir": "file://E:\\Audio\\?fatSortMode=Folders", // Valid Values are "None", "Files", "Folders", "FilesAndFolders", default is "None"
    "Exclude": [
        "Webradio",
        "Music\\Artists\\Nickelback",
        "Music\\Artists\\**\\Instrumentals"
    ],
    "WorkersRead": 8, // max number of threads to use for reading files
    "WorkersConvert": 8, // max number of threads to use for converting files
    "WorkersWrite": 1 // max number of threads to use for writing (for slow devices like HDDs, SD cards or flash drives, 1 is usually best)
}
```