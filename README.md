# music-sync-converter
Sync music to MP3 players, phones, flash drives for car radios and such. Detect and convert unsupported files automatically.
Works on Windows and Linux, macOS is untested.

## Features
- sync only files that have been added/removed/changed
- works with any directory structure (does not force you into a certain way of managing your music library)
- use a local directory or WebDAV (e.g. Nextcloud) as a source
- exclude files/directories
- use a local directory, MTP (Windows-only) or ADB as a target
- convert unsupported files on-the-fly using FFMPEG
    - decision by extension, container, codec, profile, ...
    - override codec settings for certain paths
- fast conversion due to pipelining and multithreading
- automatically embed album art from the directory into the file
- handle unsupported characters in path and tags
    - replace unsupported characters with a replacement list
    - replace unsupported characters using Unicode compatibility normalization 
- handle m3u playlists by adjusting the file path or resolving the playlist to a directory with the playlist's songs
- order the FAT32 file table (for embedded devices that don't sort directories before playing)
- normalize directory/filename capitalization (for embedded devices that sort by ASCII code instead of (case-insensitive) letter)

## Installation
### Dependencies:
- required
    - .NET 6 SDK
    - ffmpeg (including ffprobe)
- optional
    - flac (including metaflac) to handle flac files with multiple tag values correctly
    - vorbis-tools (including vorbiscomment) to handle ogg / opus files with multiple tag values correctly

All of these should be installed so they are are in PATH.

.NET SDK can be found at https://dotnet.microsoft.com/en-us/download for all platforms

On Windows, you can get those at https://www.gyan.dev/ffmpeg/builds/ https://ftp.osuosl.org/pub/xiph/releases/flac/ https://ftp.osuosl.org/pub/xiph/releases/vorbis/ and add them to the PATH variable.

On Linux/MacOS you can probably just install these using the package manager (e.g. `apt install ffmpeg flac vorbis-tools`).

## Usage:
`dotnet run --project src\MusicSyncConverter\MusicSyncConverter -- config.json`

You can split the configuration file into multiple files and supply multiple config files as arguments, which might be useful if converting for different end devices but with the same directory settings.

### Supported sources / targets
Instead of just syncing from directory to directory you can use some different file providers (or even write your own):
#### Sources
- File system: `file://` (for example `file://C:/Users/me/Music` or `file://~/Music` or `file:///home/me/Music`)
- WebDAV: `http(s)://` (for example `https://user:password@nextcloud.example.com/remote.php/dav/files/me/Music`)
#### Targets
- File system: `file://` (for example `file://F:/Music` or `file:///mnt/usb/Music`)
    - supports the query parameter `?fatSortMode=<mode>` where `<mode>` is `None`, `Folders`, `Files`, `FilesAndFolders` to sort the FAT32 table when directories change. This is useful for devices that don't sort files or directory by name.
- (on Windows) MTP using [WPD](https://docs.microsoft.com/en-us/windows/win32/windows-portable-devices): `wpd://` (for example `wpd://My Android Phone/disk/Music`)
    - Don't expect this to be rock-solid. It's MTP, what do you expect?
- ADB: `adb://` (for example `adb://abcdABCD12345678//storage/0815-ACAB/Music` where `abcdABCD12345678` is the device serial number and `/storage/0815-ACAB/Music` is the base directory)
    - this requires ADB to be installed globally (available in `PATH`) or an ADB daemon to be already running

### Excludes
You can exclude files by adding directories to the `Exclude` array in the config file.
Wildcards `*` (any directory) and `**` (any directory structure) are supported
Examples: 
- `Audio Books` ignores `Audio Books/` in the root folder
- `Music/**/Instrumentals` ignores `Music/Example Artist/Instrumentals` and `Music/Albums/Example Artist/Instrumentals`
- `Music/*/Instrumentals` ignores `Music/Example Artist/Instrumentals` but not `Music/Albums/Example Artist/Instrumentals`
- `Music/Albums/**/*.m3u` ignores `Music/Artists/Example Artist/playlist.m3u` but not `Music/Playlists/playlist.m3u`

### Formats
Format conversion works by analyzing the source file, comparing the format against the configured supported formats and converting to the configured fallback format if necessary

The supported format includes:
- `Extension`: File extension (required)
- `Codec`: Codec as reported by ffprobe, for example `aac` (required)
- `Profile`: Profile as reported by ffprobe, for example `LC` for AAC-LC
- `MaxChannels`: Max. number of audio channels
- `MaxSampleRateHz`: Max. sample rate in Hz
- `MaxBitrate`: Max. bitrate in kbit/s

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
- Reorder file table on target (required if the target device doesn't sort files and/or folders by itself and instead uses the FAT order)
- Exclude `Z:\Audio\Webradio`, `Z:\Audio\Music\Artists\Nickelback` and `Z:\Audio\Music\Artists\**\Instrumentals` (only `*` and `**` are supported)
- Copy all MP3, WMA and AAC-LC files
- Convert all other files to AAC-LC 192kbit/s
- Convert album covers to jpeg with 320x320 px max (while retaining aspect ratio)
- Replace unsupported characters in path names
- Replace non-BMP characters in path names (characters that can't be represented with UCS-2) (required for Android devices)
- Keep all characters in tags as they are
- Change every first character of file/dir names to uppercase so devices that sort case-sensitive work properly
- Resolve playlists to directories
- Override codec settings for  `Z:\Audio\Audio Books` so all audio books are converted to 64kbit/s mono to save storage space

```js
{
    "SourceDir": "file://Z:\\Audio\\",
    "TargetDir": "file://E:\\Audio\\?fatSortMode=Folders", // Valid Values are "None", "Files", "Folders", "FilesAndFolders", default is "None"
    "Exclude": [
        "Webradio",
        "Music\\Artists\\Nickelback",
        "Music\\Artists\\**\\Instrumentals"
    ],
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
        "PathCharacterLimitations": { // omit this if your device supports unicode
            "SupportedChars": "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ+-_ (),'[]!&", // all natively supported characters
            "Replacements": [ // characters that should be replaced by different characters
                {
                    "Char": "…",
                    "Replacement": "..."
                }
            ],
            "NormalizationMode": "NonBmp", // can be "None", "NonBmp", "Unsupported", "All"
            "NormalizeCase": true // change every first character of file/dir names to uppercase
        },
        "TagCharacterLimitations": null
        "ResolvePlaylists": true // convert playlists to directories with the respective files
    },
    "PathFormatOverrides": {
        "Audio Books/**": {
            "MaxBitrate": 64,
            "MaxChannels": 1
        }
    },
    "WorkersRead": 8, // max number of threads to use for reading files
    "WorkersConvert": 8, // max number of threads to use for converting files
    "WorkersWrite": 1 // max number of threads to use for writing (for slow devices like HDDs, SD cards or flash drives, 1 is usually best)
}
```