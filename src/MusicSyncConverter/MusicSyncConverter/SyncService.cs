using Microsoft.Extensions.FileProviders;
using MusicSyncConverter.Config;
using MusicSyncConverter.FileProviders;
using MusicSyncConverter.FileProviders.Abstractions;
using MusicSyncConverter.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace MusicSyncConverter
{
    class SyncService
    {
        private readonly FileProviderFactory _fileProviderFactory;
        private readonly SyncTargetFactory _syncTargetFactory;
        private readonly TextSanitizer _sanitizer;
        private readonly MediaConverter _converter;
        private readonly PathMatcher _pathMatcher;
        private readonly PlaylistParser _playlistParser;
        private static readonly TimeSpan _fileTimestampDelta = TimeSpan.FromSeconds(2); // FAT32 write time has 2 seconds precision

        public SyncService()
        {
            _fileProviderFactory = new FileProviderFactory();
            _syncTargetFactory = new SyncTargetFactory();
            _sanitizer = new TextSanitizer();
            _converter = new MediaConverter();
            _pathMatcher = new PathMatcher();
            _playlistParser = new PlaylistParser();
        }

        public async Task Run(SyncConfig config, CancellationToken upstreamCancellationToken)
        {
            using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(upstreamCancellationToken);
            var cancellationToken = cancellationTokenSource.Token;
            CleanupTempDir(cancellationToken);

            //set up pipeline
            var readOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = config.WorkersRead ?? 1,
                MaxDegreeOfParallelism = config.WorkersRead ?? 1,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            };

            var workerOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = config.WorkersConvert ?? Environment.ProcessorCount,
                MaxDegreeOfParallelism = config.WorkersConvert ?? Environment.ProcessorCount,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            };

            var writeOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = config.WorkersWrite ?? 1,
                MaxDegreeOfParallelism = config.WorkersWrite ?? 1,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            };

            using var handledFilesComplete = new CancellationTokenSource();
            var handledFiles = new ConcurrentBag<FileSourceTargetInfo>();
            var updatedDirs = new ConcurrentBag<string>();
            var infoLogMessages = new ConcurrentBag<string>();

            var fileProvider = _fileProviderFactory.Get(config.SourceDir);
            using (fileProvider as IDisposable)
            {
                var syncTarget = _syncTargetFactory.Get(config.TargetDir);
                using (syncTarget as IDisposable)
                {
                    var targetCaseSensitive = syncTarget.IsCaseSensitive();
                    var sourcePathComparer = new PathComparer(false); // TODO: determine if this is an issue
                    var targetPathComparer = new PathComparer(targetCaseSensitive);

                    // pipeline is written bottom to top here so it can be linked properly

                    // --- file writing ---
                    var writeBlock = new ActionBlock<OutputFile>(file => WriteFile(file, syncTarget, cancellationToken), writeOptions);

                    // --- song handling ---
                    var convertSongBlock = new TransformManyBlock<SongConvertWorkItem, OutputFile>(async x => new[] { await ConvertSong(x, config, infoLogMessages, handledFiles, cancellationToken) }.Where(y => y != null)!, workerOptions);
                    convertSongBlock.LinkTo(writeBlock, new DataflowLinkOptions { PropagateCompletion = false });

                    var readSongBlock = new TransformBlock<SongReadWorkItem, SongConvertWorkItem>(async x => await ReadSong(x, fileProvider, cancellationToken), readOptions);
                    readSongBlock.LinkTo(convertSongBlock, new DataflowLinkOptions { PropagateCompletion = true });

                    var compareSongBlock = new TransformManyBlock<SongSyncInfo[], SongReadWorkItem>(x => CompareSongDates(x, config, targetPathComparer, syncTarget, infoLogMessages), readOptions);
                    compareSongBlock.LinkTo(readSongBlock, new DataflowLinkOptions { PropagateCompletion = true });

                    // group files by their directory before comparing so we don't have to do a directory listing for every file
                    var groupSongsByDirectoryBlock = CustomBlocks.GetGroupByBlock<SongSyncInfo, string>(x => Path.GetDirectoryName(x.TargetPath)!, targetPathComparer);
                    groupSongsByDirectoryBlock.LinkTo(compareSongBlock, new DataflowLinkOptions { PropagateCompletion = false });

                    var getSyncInfoBlock = new TransformBlock<SourceFileInfo, SongSyncInfo>(x => GetSyncInfo(x), workerOptions);
                    getSyncInfoBlock.LinkTo(groupSongsByDirectoryBlock, new DataflowLinkOptions { PropagateCompletion = true });

                    // --- playlist handling ---
                    var readPlaylistBlock = new TransformManyBlock<SourceFileInfo, Playlist>(async file => new[] { await ReadPlaylist(file, fileProvider) }.Where(x => x != null)!, readOptions);

                    IDataflowBlock? resolvePlaylistBlock = null;
                    IDataflowBlock? convertPlaylistBlock = null;
                    if (config.DeviceConfig.ResolvePlaylists)
                    {
                        var resolvePlaylistBlockSpecific = new ActionBlock<Playlist>(async x => await ResolvePlaylist(x, fileProvider, compareSongBlock), workerOptions);
                        readPlaylistBlock.LinkTo(resolvePlaylistBlockSpecific, new DataflowLinkOptions { PropagateCompletion = true });
                        resolvePlaylistBlock = resolvePlaylistBlockSpecific;
                    }
                    else
                    {
                        var convertPlaylistBlockSpecific = new TransformManyBlock<Playlist, OutputFile>(async x => new[] { await ConvertPlaylist(x, config, syncTarget, sourcePathComparer, handledFiles, handledFilesComplete.Token, infoLogMessages, cancellationToken) }.Where(x => x != null)!, workerOptions);
                        readPlaylistBlock.LinkTo(convertPlaylistBlockSpecific, new DataflowLinkOptions { PropagateCompletion = true });
                        convertPlaylistBlockSpecific.LinkTo(writeBlock, new DataflowLinkOptions { PropagateCompletion = false });
                        convertPlaylistBlock = convertPlaylistBlockSpecific;
                    }

                    var fileRouterBlock = new ActionBlock<SourceFileInfo>(async file =>
                    {
                        await getSyncInfoBlock.SendAsync(file);
                        await readPlaylistBlock.SendAsync(file);
                    }, workerOptions);

                    Console.WriteLine("Checking for new/changed files");

                    // start pipeline by adding files to check for changes
                    _ = ReadDirs(config, fileProvider, fileRouterBlock, targetCaseSensitive, cancellationToken);
                    // wait until every pipeline element is done
                    try
                    {
                        // if there is a fault during comparison/reading/conversion/writing, cancel all other pipeline elements immediately to avoid freezing
                        _ = writeBlock.Completion.ContinueWith(x => HandlePipelineError(x.Exception!, cancellationTokenSource), TaskContinuationOptions.OnlyOnFaulted);
                        _ = convertSongBlock.Completion.ContinueWith(x => HandlePipelineError(x.Exception!, cancellationTokenSource), TaskContinuationOptions.OnlyOnFaulted);
                        _ = convertPlaylistBlock?.Completion.ContinueWith(x => HandlePipelineError(x.Exception!, cancellationTokenSource), TaskContinuationOptions.OnlyOnFaulted);

                        await fileRouterBlock.Completion;
                        getSyncInfoBlock.Complete();
                        readPlaylistBlock.Complete();

                        // GetSyncInfo and ResolvePlaylist can add new CompareSong items, so we have to wait for those before completing CompareSong
                        await groupSongsByDirectoryBlock.Completion;
                        if (resolvePlaylistBlock != null)
                        {
                            await resolvePlaylistBlock.Completion;
                        }

                        compareSongBlock.Complete();

                        // wait until all elements that can write are done before completing write block
                        await Task.WhenAll(compareSongBlock.Completion, convertSongBlock.Completion);

                        // signal that all songs have been converted so playlist converter
                        // waiting for songs knows they're missing if they aren't there now.
                        handledFilesComplete.Cancel();

                        if (convertPlaylistBlock != null)
                        {
                            await convertPlaylistBlock.Completion;
                        }

                        writeBlock.Complete();
                        await writeBlock.Completion;
                    }
                    catch (Exception)
                    {
                        // cancel everything as faults only get propagated to the blocks after the fault
                        // and we need all blocks to stop so we can exit the application
                        cancellationTokenSource.Cancel();
                        throw;
                    }

                    // delete additional files and empty directories
                    Console.WriteLine("Checking for leftover files");
                    DeleteAdditionalFiles(syncTarget, handledFiles, targetPathComparer, cancellationToken);
                    Console.WriteLine("Checking for empty subdirectories");
                    DeleteEmptySubdirectories("", syncTarget, cancellationToken);

                    await syncTarget.Complete(cancellationToken);
                }
            }

            foreach (var infoLogMessage in infoLogMessages.Distinct().OrderBy(x => x))
            {
                Console.WriteLine(infoLogMessage);
            }
        }

        private void HandlePipelineError(AggregateException ex, CancellationTokenSource cancellationTokenSource)
        {
            var flattened = ex.Flatten();
            if (flattened.InnerExceptions.Count == 1)
            {
                Console.WriteLine(flattened.InnerException);
            }
            else
            {
                Console.WriteLine(flattened);
            }
            cancellationTokenSource.Cancel();
        }

        private async Task ReadDirs(SyncConfig config, IFileProvider fileProvider, ITargetBlock<SourceFileInfo> targetBlock, bool targetCaseSensitive, CancellationToken cancellationToken)
        {
            try
            {
                var r = new Random();
                var files = ReadDir(config, fileProvider, "", targetCaseSensitive, cancellationToken);
                //.OrderBy(x => r.Next()); // randomize order so IO- and CPU-heavy actions are more balanced
                foreach (var file in files)
                {
                    await targetBlock.SendAsync(file, cancellationToken);
                }
                targetBlock.Complete();
            }
            catch (Exception ex)
            {
                targetBlock.Fault(ex);
                throw;
            }
        }

        private void CleanupTempDir(CancellationToken cancellationToken)
        {
            var thresholdDate = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var file in Directory.GetFiles(TempFileHelper.GetTempPath()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTimeUtc < thresholdDate)
                    fileInfo.Delete();
            }
        }

        private IEnumerable<SourceFileInfo> ReadDir(SyncConfig config, IFileProvider fileProvider, string dir, bool caseSensitive, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (config.Exclude?.Any(glob => _pathMatcher.Matches(glob, dir, caseSensitive)) ?? false)
            {
                yield break;
            }
            var directoryContents = fileProvider.GetDirectoryContents(dir).ToList();
            foreach (var file in directoryContents.Where(x => !x.IsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string filePath = Path.Join(dir, file.Name);
                if (config.Exclude?.Any(glob => _pathMatcher.Matches(glob, filePath, caseSensitive)) ?? false)
                {
                    continue;
                }

                yield return new SourceFileInfo
                {
                    Path = filePath,
                    ModifiedDate = file.LastModified
                };
            }

            foreach (var subDir in directoryContents.Where(x => x.IsDirectory))
            {
                foreach (var file in ReadDir(config, fileProvider, Path.Join(dir, subDir.Name), caseSensitive, cancellationToken))
                {
                    yield return file;
                }
            }
        }

        private async Task<Playlist?> ReadPlaylist(SourceFileInfo playlistFile, IFileProvider fileProvider)
        {
            var playlistExtensions = new[] { ".m3u", ".m3u8" };
            if (!playlistExtensions.Contains(Path.GetExtension(playlistFile.Path)))
            {
                return null;
            }

            var fileInfo = fileProvider.GetFileInfo(playlistFile.Path);
            IReadOnlyList<PlaylistSong> playlistSongs;
            using (var sr = new StreamReader(fileInfo.CreateReadStream(), Encoding.UTF8))
            {
                playlistSongs = await _playlistParser.ParseM3u(sr);
            }
            return new Playlist(playlistFile, playlistSongs);
        }

        private async Task ResolvePlaylist(Playlist playlist, IFileProvider fileProvider, ITargetBlock<SongSyncInfo[]> songOutput)
        {
            try
            {
                var playlistDir = Path.GetDirectoryName(playlist.PlaylistFileInfo.Path);
                var digits = playlist.Songs.Count.ToString(CultureInfo.InvariantCulture).Length;
                var i = 1;
                foreach (var song in playlist.Songs)
                {
                    if (Path.IsPathRooted(song.Path))
                        throw new InvalidOperationException($"Playlist song paths must not be absolute! In {playlist.PlaylistFileInfo.Path}");

                    var sourcePath = Path.Join(playlistDir, song.Path);
                    var songFileInfo = fileProvider.GetFileInfo(sourcePath);
                    var songName = song.Name != null ? (song.Name + ".tmp") : Path.GetFileName(song.Path);
                    var songFileName = $"{i.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')} {songName}";
                    var songTargetPath = Path.Join(playlistDir, Path.GetFileNameWithoutExtension(playlist.PlaylistFileInfo.Path), songFileName);
                    var syncInfo = new SongSyncInfo
                    {
                        SourceFileInfo = new SourceFileInfo
                        {
                            Path = sourcePath,
                            ModifiedDate = songFileInfo.LastModified
                        },
                        TargetPath = songTargetPath
                    };
                    await songOutput.SendAsync(new[] { syncInfo });
                    i++;
                }
            }
            catch (Exception ex)
            {
                songOutput.Fault(ex);
                throw;
            }
        }

        private async Task<OutputFile?> ConvertPlaylist(Playlist playlist, SyncConfig syncConfig, ISyncTarget syncTarget, PathComparer sourcePathComparer, ConcurrentBag<FileSourceTargetInfo> handledFiles, CancellationToken handledFilesCompleteToken, IProducerConsumerCollection<string> infoLogMessages, CancellationToken cancellationToken)
        {
            var playlistFile = playlist.PlaylistFileInfo;

            var playlistPath = _sanitizer.SanitizeText(syncConfig.DeviceConfig.CharacterLimitations, playlistFile.Path, true, out _);
            var targetPlaylistFileInfo = syncTarget.GetFileInfo(playlistPath);
            if (targetPlaylistFileInfo.Exists && FileDatesEqual(targetPlaylistFileInfo.LastModified, playlistFile.ModifiedDate))
            {
                handledFiles.Add(new FileSourceTargetInfo(playlistFile.Path, playlistPath));
                return null;
            }

            var allSongsFound = true;

            var tmpFilePath = TempFileHelper.GetTempFilePath();
            using (var sw = File.CreateText(tmpFilePath))
            {
                sw.NewLine = "\r\n";
                sw.WriteLine("#EXTM3U");
                foreach (var song in playlist.Songs)
                {
                    string? playlistDirectoryPath = Path.GetDirectoryName(playlistFile.Path);
                    if (playlistDirectoryPath == null)
                        throw new ArgumentException($"Playlist path {playlistDirectoryPath} is invalid");
                    var playlistSongPath = Path.Join(playlistDirectoryPath, song.Path);

                    string? targetFilePath;
                    while (true)
                    {
                        var handledFilesComplete = handledFilesCompleteToken.IsCancellationRequested;
                        var songMapping = handledFiles.FirstOrDefault(x => sourcePathComparer.Equals(x.SourcePath, playlistSongPath));
                        if (songMapping != null)
                        {
                            targetFilePath = songMapping.TargetPath;
                            break;
                        }
                        if (handledFilesComplete)
                        {
                            infoLogMessages.TryAdd($"Missing song {playlistSongPath} referenced in playlist {playlistFile.Path}");
                            allSongsFound = false;
                            targetFilePath = null;
                            break;
                        }
                        await Task.Delay(1000, cancellationToken);
                    }
                    if (targetFilePath == null)
                        continue;

                    string songName = song.Name ?? Path.GetFileNameWithoutExtension(song.Path);
                    sw.WriteLine($"#EXTINF:0,{_sanitizer.SanitizeText(syncConfig.DeviceConfig.CharacterLimitations, songName, false, out _)}");
                    sw.WriteLine(Path.GetRelativePath(playlistDirectoryPath, targetFilePath));
                }
            }

            var outFile = new OutputFile
            {
                TempFilePath = tmpFilePath,
                Path = playlistPath,
                ModifiedDate = allSongsFound ? playlistFile.ModifiedDate : DateTimeOffset.UtcNow // if songs were missing, try again next sync
            };
            handledFiles.Add(new FileSourceTargetInfo(playlistFile.Path, playlistPath));
            return outFile;
        }

        private SongSyncInfo GetSyncInfo(SourceFileInfo sourceFile)
        {
            return new SongSyncInfo
            {
                SourceFileInfo = sourceFile,
                TargetPath = sourceFile.Path
            };
        }

        private IEnumerable<SongReadWorkItem> CompareSongDates(SongSyncInfo[] syncInfos, SyncConfig config, PathComparer targetPathComparer, ISyncTarget syncTarget, IProducerConsumerCollection<string> infoLogMessages)
        {
            if (syncInfos.Length == 0)
                yield break;
            var exampleSyncInfo = syncInfos[0];

            var targetDirPath = Path.GetDirectoryName(_sanitizer.SanitizeText(config.DeviceConfig.CharacterLimitations, exampleSyncInfo.TargetPath, true, out _)) ?? throw new ArgumentException("DirectoryName is null");

            var directoryContents = syncTarget.GetDirectoryContents(targetDirPath);
            var files = directoryContents.ToList();

            foreach (var syncInfo in syncInfos)
            {
                if (Path.GetDirectoryName(exampleSyncInfo.TargetPath) != Path.GetDirectoryName(syncInfo.TargetPath))
                    throw new InvalidOperationException("CompareSongDates can only compare batches of files in the same directory");

                if (!config.SourceExtensions.Contains(Path.GetExtension(syncInfo.SourceFileInfo.Path), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var targetPath = _sanitizer.SanitizeText(config.DeviceConfig.CharacterLimitations, syncInfo.TargetPath, true, out var hasUnsupportedChars);
                if (hasUnsupportedChars)
                    infoLogMessages.TryAdd($"Unsupported chars in path: {syncInfo.TargetPath}");

                var targetInfos = directoryContents.Exists ? files.Where(x => targetPathComparer.FileNameEquals(Path.GetFileNameWithoutExtension(targetPath), Path.GetFileNameWithoutExtension(x.Name))).ToArray() : Array.Empty<IFileInfo>();

                if (targetInfos.Length == 1 && FileDatesEqual(syncInfo.SourceFileInfo.ModifiedDate, targetInfos[0].LastModified))
                {
                    // only one target item, so check if it is up to date
                    yield return new SongReadWorkItem
                    {
                        ActionType = CompareResultType.Keep,
                        SourceFileInfo = syncInfo.SourceFileInfo,
                        TargetFilePath = Path.Join(targetDirPath, targetInfos[0].Name)
                    };
                }
                else
                {
                    // zero or multiple target files, so just replace it
                    yield return new SongReadWorkItem
                    {
                        ActionType = CompareResultType.Replace,
                        SourceFileInfo = syncInfo.SourceFileInfo,
                        TargetFilePath = targetPath
                    };
                }
            }
        }

        private bool FileDatesEqual(DateTimeOffset dateTime, DateTimeOffset dateTime1)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                // this is really stupid but to handle this properly would be a lot of effort
                // https://twitter.com/patagona/status/1516123536275910658
                return (dateTime - dateTime1).Duration() <= _fileTimestampDelta ||
                    (dateTime - dateTime1 - TimeSpan.FromHours(1)).Duration() <= _fileTimestampDelta ||
                    (dateTime - dateTime1 + TimeSpan.FromHours(1)).Duration() <= _fileTimestampDelta;
            }

            return (dateTime - dateTime1).Duration() <= _fileTimestampDelta;
        }

        private async Task<SongConvertWorkItem> ReadSong(SongReadWorkItem workItem, IFileProvider fileProvider, CancellationToken cancellationToken)
        {
            SongConvertWorkItem toReturn;
            switch (workItem.ActionType)
            {
                case CompareResultType.Keep:
                    toReturn = new SongConvertWorkItem
                    {
                        ActionType = ConvertActionType.Keep,
                        SourceFileInfo = workItem.SourceFileInfo,
                        TargetFilePath = workItem.TargetFilePath
                    };
                    break;
                case CompareResultType.Replace:
                    Console.WriteLine($"--> Read {workItem.SourceFileInfo.Path}");

                    string tmpFilePath;
                    using (var inFile = fileProvider.GetFileInfo(workItem.SourceFileInfo.Path).CreateReadStream())
                    {
                        tmpFilePath = await TempFileHelper.CopyToTempFile(inFile, cancellationToken);
                    }

                    string directoryPath = Path.GetDirectoryName(workItem.SourceFileInfo.Path) ?? throw new ArgumentException("DirectoryName is null");
                    string? albumCoverPath = await GetAlbumCoverPath(directoryPath, fileProvider, cancellationToken);

                    toReturn = new SongConvertWorkItem
                    {
                        ActionType = ConvertActionType.RemuxOrConvert,
                        SourceFileInfo = workItem.SourceFileInfo,
                        SourceTempFilePath = tmpFilePath,
                        TargetFilePath = workItem.TargetFilePath,
                        AlbumArtPath = albumCoverPath
                    };
                    Console.WriteLine($"<-- Read {workItem.SourceFileInfo.Path}");
                    break;
                default:
                    throw new ArgumentException("Invalid ReadActionType");
            }
            return toReturn;
        }

        private static async Task<string?> GetAlbumCoverPath(string dirPath, IFileProvider fileProvider, CancellationToken cancellationToken)
        {
            var coverVariants = new[] { "cover.png", "cover.jpg", "folder.jpg" };
            foreach (var coverVariant in coverVariants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var coverFileInfo = fileProvider.GetFileInfo(Path.Join(dirPath, coverVariant));
                if (coverFileInfo.Exists)
                {
                    if (coverFileInfo.PhysicalPath != null)
                    {
                        return coverFileInfo.PhysicalPath;
                    }
                    else
                    {
                        using (var coverStream = coverFileInfo.CreateReadStream())
                        {
                            return await TempFileHelper.CopyToTempFile(coverStream, cancellationToken);
                        }
                    }
                }
            }
            return null;
        }

        private async Task<OutputFile?> ConvertSong(SongConvertWorkItem workItem, SyncConfig config, IProducerConsumerCollection<string> infoLogMessages, ConcurrentBag<FileSourceTargetInfo> handledFiles, CancellationToken cancellationToken)
        {
            try
            {
                switch (workItem.ActionType)
                {
                    case ConvertActionType.Keep:
                        handledFiles.Add(new FileSourceTargetInfo(workItem.SourceFileInfo.Path, workItem.TargetFilePath));
                        return null;
                    case ConvertActionType.RemuxOrConvert:
                        {
                            var sw = Stopwatch.StartNew();
                            Console.WriteLine($"--> Convert {workItem.SourceFileInfo.Path}");
                            var outFile = await _converter.RemuxOrConvert(config, workItem, infoLogMessages, cancellationToken);
                            if (outFile != null)
                                handledFiles.Add(new FileSourceTargetInfo(workItem.SourceFileInfo.Path, outFile.Path));
                            sw.Stop();
                            Console.WriteLine($"<-- Convert ({sw.ElapsedMilliseconds}ms) {workItem.SourceFileInfo.Path}");
                            return outFile;
                        }
                    default:
                        throw new ArgumentException("Invalid ConvertActionType");
                }
            }
            finally
            {
                if (workItem.SourceTempFilePath != null)
                {
                    File.Delete(workItem.SourceTempFilePath);
                }
            }
        }

        private async Task WriteFile(OutputFile file, ISyncTarget syncTarget, CancellationToken cancellationToken)
        {
            try
            {
                Console.WriteLine($"--> Write {file.Path}");

                var sw = Stopwatch.StartNew();
                long fileSize;
                using (var tmpFile = File.OpenRead(file.TempFilePath))
                {
                    fileSize = tmpFile.Length;
                    await syncTarget.WriteFile(file.Path, tmpFile, file.ModifiedDate, cancellationToken);
                }
                sw.Stop();

                File.Delete(file.TempFilePath);
                Console.WriteLine($"<-- Write ({(fileSize / sw.Elapsed.TotalSeconds / 1024 / 1024).ToString("F2", CultureInfo.InvariantCulture)} MiB/s) {file.Path}");
            }
            catch (TaskCanceledException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }

        private void DeleteAdditionalFiles(ISyncTarget syncTarget, ConcurrentBag<FileSourceTargetInfo> handledFiles, IEqualityComparer<string> targetPathComparer, CancellationToken cancellationToken)
        {
            var files = GetAllFiles("", syncTarget);
            var toDelete = new List<IFileInfo>();
            foreach (var (path, file) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (PathUtils.GetPathStack(path).Any(x => x.StartsWith('.')))
                {
                    // hidden file/dir
                    continue;
                }

                if (!handledFiles.Any(x => targetPathComparer.Equals(x.TargetPath, path)))
                {
                    toDelete.Add(file);
                    Console.WriteLine($"Delete {path}");
                }
            }
            syncTarget.Delete(toDelete, cancellationToken);
        }

        private IEnumerable<(string Path, IFileInfo File)> GetAllFiles(string directoryPath, ISyncTarget syncTarget)
        {
            foreach (var fileInfo in syncTarget.GetDirectoryContents(directoryPath))
            {
                var filePath = Path.Join(directoryPath, fileInfo.Name);
                if (fileInfo.IsDirectory)
                {
                    foreach (var file in GetAllFiles(filePath, syncTarget))
                    {
                        yield return file;
                    }
                }
                else
                {
                    yield return (filePath, fileInfo);
                }
            }
        }

        private void DeleteEmptySubdirectories(string path, ISyncTarget syncTarget, CancellationToken cancellationToken)
        {
            foreach (var item in syncTarget.GetDirectoryContents(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!item.IsDirectory)
                    continue;

                string subDir = Path.Join(path, item.Name);
                DeleteEmptySubdirectories(subDir, syncTarget, cancellationToken);
                if (!syncTarget.GetDirectoryContents(subDir).Any())
                {
                    Console.WriteLine($"Delete {subDir}");
                    syncTarget.Delete(item, cancellationToken);
                }
            }
        }

        private class FileSourceTargetInfo
        {
            public FileSourceTargetInfo(string sourcePath, string targetPath)
            {
                SourcePath = sourcePath;
                TargetPath = targetPath;
            }
            public string SourcePath { get; }
            public string TargetPath { get; }
            public override string ToString()
            {
                return SourcePath == TargetPath ? SourcePath : $"{SourcePath} -> {TargetPath}";
            }
        }
    }
}
