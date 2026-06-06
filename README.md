# music-sync-converter
rsync for your music library. Sync your music library to MP3 players, phones, flash drives etc. while automatically converting unsupported files.

## Features
- sync only files that have been added/removed/changed
- works with any directory structure (does not force you into a certain way of managing your music library)
- exclude files/directories
- use a local directory or WebDAV (e.g. Nextcloud) as a source
- use a local directory, MTP (Windows-only) or ADB as a target
- convert unsupported files on-the-fly using FFMPEG
    - detect file support by extension, container, codec, profile, ...
    - override codec settings for certain paths
- fast read/convert/write due to buffering and parallelization
- automatically embed album art from the directory into the file
- handle miscellaneous device limitations (configurable)
    - unsupported file formats/containers
    - unsupported characters in path and tags
    - missing playlist support (can resolve playlists to directories)
    - sorting by FAT32 file table order
    - case-sensitive sorting
    - limited directory depth
    - album art stretching
- 100% AI-free

## Installation
This project works on Windows and Linux, macOS is untested.

### Dependencies:
- required
    - .NET 10 SDK
    - ffmpeg (including ffprobe)
- optional
    - adb (for sync to Android devices via adb)
    - flac (including metaflac) to handle flac files with multiple tag values correctly

All of these should be installed so they are are in PATH.

.NET SDK can be found at https://dotnet.microsoft.com/en-us/download for all platforms

Windows:
- ffmpeg: https://www.gyan.dev/ffmpeg/builds/
- flac: https://ftp.osuosl.org/pub/xiph/releases/flac/ 

These have to be added to PATH manually.

On Linux/MacOS you can probably just install these using the package manager (e.g. `apt install ffmpeg flac`).

## Usage:

To use this, a configuration file specifying (at least) the source and target directories has to be created once (see [Wiki: Configuration](https://github.com/patagonaa/music-sync-converter/wiki/Configuration)).

Some predefined device configurations (including supported file formats, codecs, etc.) for Android and a few car stereos are available in src/MusicSyncConverter/MusicSyncConverter/configs/.

Once the config(s) are ready, you can run this using:

`dotnet run --project src/MusicSyncConverter/MusicSyncConverter -- config.json [...]`

## Support

Please consider donating (via GitHub Sponsors) if this project is useful to you.

You can also support this project in other ways:

- by adding device configurations for devices you have available for testing
- by reporting bugs (via issues)
- by requesting features (via issues/discussions)
- by contributing code directly (via pull requests)

AI-generated contributions (code or issues) are not welcome and will not be considered.