﻿using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using MusicSyncConverter.Config;
using MusicSyncConverter.Conversion;
using MusicSyncConverter.FileProviders;
using MusicSyncConverter.FileProviders.SyncTargets;
using MusicSyncConverter.Models;
using MusicSyncConverter.Playlists;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace MusicSyncConverter
{
    class SyncService
    {
        private static readonly TimeSpan _fileTimestampDelta = TimeSpan.FromSeconds(2); // FAT32 write time has 2 seconds precision

        private readonly FileProviderFactory _fileProviderFactory;
        private readonly SyncTargetFactory _syncTargetFactory;
        private readonly TextSanitizer _sanitizer;
        private readonly PathTransformer _pathTransformer;
        private readonly MediaConverter _converter;
        private readonly PlaylistParser _playlistParser;
        private readonly PlaylistWriter _playlistWriter;
        private readonly ITempFileSession _tempFileSession;
        private readonly ILogger _logger;

        public SyncService(ITempFileSession tempFileSession, ILogger logger)
        {
            _fileProviderFactory = new FileProviderFactory();
            _syncTargetFactory = new SyncTargetFactory();
            _sanitizer = new TextSanitizer();
            _pathTransformer = new PathTransformer(_sanitizer);
            _converter = new MediaConverter(tempFileSession, logger);
            _playlistParser = new PlaylistParser();
            _playlistWriter = new PlaylistWriter();
            _tempFileSession = tempFileSession;
            _logger = logger;
        }

        public async Task Run(SyncConfig config, CancellationToken upstreamCancellationToken)
        {
            using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(upstreamCancellationToken);
            var cancellationToken = cancellationTokenSource.Token;

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

            var fileProvider = _fileProviderFactory.Get(config.SourceDir);
            using (fileProvider as IDisposable)
            {
                var syncTarget = await _syncTargetFactory.Get(config.TargetDir, cancellationToken);
                using (syncTarget as IDisposable)
                {
                    var sourceCaseSensitive = true; // should only be an issue if the target is case sensitive but the source isn't
                    var targetCaseSensitive = await syncTarget.IsCaseSensitive(cancellationToken);

                    var compareCaseSensitive = sourceCaseSensitive && targetCaseSensitive;
                    var pathComparer = new PathComparer(compareCaseSensitive);
                    var pathMatcher = new PathMatcher(compareCaseSensitive);

                    // pipeline is written bottom to top here so it can be linked properly

                    // --- file writing ---
                    var writeBlock = new ActionBlock<OutputFile>(file => WriteFile(file, syncTarget, cancellationToken), writeOptions);

                    // --- song handling ---
                    var convertSongBlock = new TransformManyBlock<SongConvertWorkItem, OutputFile>(async x => await FilterNull(AddLogContext(x.SourceFileInfo, x.TargetFilePath, () => ConvertSong(pathMatcher, x, config, handledFiles, cancellationToken))), workerOptions);
                    convertSongBlock.LinkTo(writeBlock, new DataflowLinkOptions { PropagateCompletion = false });

                    var readSongBlock = new TransformBlock<SongReadWorkItem, SongConvertWorkItem>(async x => await AddLogContext(x.SourceFileInfo, x.TargetFilePath, () => ReadSong(x, fileProvider, cancellationToken)), readOptions);
                    readSongBlock.LinkTo(convertSongBlock, new DataflowLinkOptions { PropagateCompletion = true });

                    var compareSongBlock = new TransformManyBlock<SongSyncInfo[], SongReadWorkItem>(async x => await CompareSongDates(x, config, pathComparer, syncTarget, cancellationToken).ToListAsync(), readOptions);
                    compareSongBlock.LinkTo(readSongBlock, new DataflowLinkOptions { PropagateCompletion = true });

                    // group files by their directory before comparing so we don't have to do a directory listing for every file
                    var groupSongsByDirectoryBlock = CustomBlocks.GetGroupByBlock<SongSyncInfo, NormalizedPath>(x => new NormalizedPath(Path.GetDirectoryName(x.TargetPath)!), pathComparer, cancellationToken);
                    groupSongsByDirectoryBlock.LinkTo(compareSongBlock, new DataflowLinkOptions { PropagateCompletion = false });

                    var getSyncInfoBlock = new TransformManyBlock<SourceFileInfo, SongSyncInfo>(x => FilterNull(GetSyncInfo(x, config)), workerOptions);
                    getSyncInfoBlock.LinkTo(groupSongsByDirectoryBlock, new DataflowLinkOptions { PropagateCompletion = true });

                    // --- playlist handling ---
                    var readPlaylistBlock = new TransformManyBlock<SourceFileInfo, Playlist>(async file => await FilterNull(AddLogContext(file, null, () => ReadPlaylist(file, fileProvider))), readOptions);

                    IDataflowBlock? resolvePlaylistBlock = null;
                    IDataflowBlock? convertPlaylistBlock = null;
                    if (config.DeviceConfig.ResolvePlaylists)
                    {
                        var resolvePlaylistBlockSpecific = new ActionBlock<Playlist>(async x => await AddLogContext(x.PlaylistFileInfo, null, () => ResolvePlaylist(x, fileProvider, compareSongBlock)), workerOptions);
                        readPlaylistBlock.LinkTo(resolvePlaylistBlockSpecific, new DataflowLinkOptions { PropagateCompletion = true });
                        resolvePlaylistBlock = resolvePlaylistBlockSpecific;
                    }
                    else
                    {
                        var convertPlaylistOptions = new ExecutionDataflowBlockOptions
                        {
                            BoundedCapacity = -1, // if this has a bound, too many playlists (with not-yet-converted songs) at once may block readPlaylistBlock, which blocks fileRouterBlock, which blocks everything.
                            MaxDegreeOfParallelism = config.WorkersConvert ?? Environment.ProcessorCount,
                            EnsureOrdered = false,
                            CancellationToken = cancellationToken
                        };

                        var convertPlaylistBlockSpecific = new TransformManyBlock<Playlist, OutputFile>(async x => await FilterNull(AddLogContext(x.PlaylistFileInfo, null, () => ConvertPlaylist(x, config, syncTarget, pathComparer, handledFiles, handledFilesComplete.Token, cancellationToken))), convertPlaylistOptions);
                        readPlaylistBlock.LinkTo(convertPlaylistBlockSpecific, new DataflowLinkOptions { PropagateCompletion = true });
                        convertPlaylistBlockSpecific.LinkTo(writeBlock, new DataflowLinkOptions { PropagateCompletion = false });
                        convertPlaylistBlock = convertPlaylistBlockSpecific;
                    }

                    var fileRouterBlock = new ActionBlock<SourceFileInfo>(async file =>
                    {
                        await getSyncInfoBlock.SendAsync(file, cancellationToken);
                        await readPlaylistBlock.SendAsync(file, cancellationToken);
                    }, workerOptions);

                    Console.WriteLine("Checking for new/changed files");
                    try
                    {
                        // if there is a fault during comparison/reading/conversion/writing, cancel all other pipeline elements immediately to avoid freezing
                        _ = writeBlock.Completion.ContinueWith(x => HandlePipelineError(x.Exception!, cancellationTokenSource), TaskContinuationOptions.OnlyOnFaulted);
                        _ = convertSongBlock.Completion.ContinueWith(x => HandlePipelineError(x.Exception!, cancellationTokenSource), TaskContinuationOptions.OnlyOnFaulted);
                        _ = convertPlaylistBlock?.Completion.ContinueWith(x => HandlePipelineError(x.Exception!, cancellationTokenSource), TaskContinuationOptions.OnlyOnFaulted);

                        // start pipeline by adding files to check for changes
                        await ReadDirs(config, fileProvider, fileRouterBlock, pathMatcher, cancellationToken);

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

                        var tasks = new[]
                            {
                                writeBlock,
                                convertSongBlock,
                                readSongBlock,
                                compareSongBlock,
                                groupSongsByDirectoryBlock,
                                getSyncInfoBlock,
                                readPlaylistBlock,
                                resolvePlaylistBlock,
                                convertPlaylistBlock,
                                fileRouterBlock
                            }
                            .Where(x => x != null)
                            .Select(x => x!.Completion)
                            .ToList();

                        // wait for all blocks to be fully cancelled so we don't return before everything is done/canceled
                        await Task.WhenAll(tasks);
                        throw;
                    }

                    // delete additional files and empty directories
                    Console.WriteLine("Checking for leftover files/directories");
                    await DeleteAdditional(syncTarget, handledFiles, pathComparer, pathMatcher, config.KeepInTarget, cancellationToken);

                    await syncTarget.Complete(cancellationToken);
                }
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
            if (!cancellationTokenSource.IsCancellationRequested)
                cancellationTokenSource.Cancel();
        }

        private IDisposable? StartLogContext(SourceFileInfo sourceFile, string? targetFile)
        {
            return _logger.BeginScope(new Dictionary<string, object?>
            {
                { "SourceFile", sourceFile.Path },
                { "TargetFile", targetFile }
            });
        }

        private async Task<T> AddLogContext<T>(SourceFileInfo sourceFile, string? targetFile, Func<Task<T>> action)
        {
            T result;
            using (StartLogContext(sourceFile, targetFile))
            {
                result = await action();
            }
            return result;
        }

        private async Task AddLogContext(SourceFileInfo sourceFile, string? targetFile, Func<Task> action)
        {
            using (StartLogContext(sourceFile, targetFile))
            {
                await action();
            }
        }

        private static IEnumerable<T> FilterNull<T>(T? value)
        {
            if (value != null)
                yield return value;
        }

        private static async Task<IEnumerable<T>> FilterNull<T>(Task<T?> task)
        {
            var result = await task;
            return FilterNull(result);
        }

        private async Task ReadDirs(SyncConfig config, IFileProvider fileProvider, ITargetBlock<SourceFileInfo> targetBlock, PathMatcher pathMatcher, CancellationToken cancellationToken)
        {
            try
            {
                var files = ReadDir("");
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

            IEnumerable<SourceFileInfo> ReadDir(string dir)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (config.Exclude?.Any(glob => pathMatcher.Matches(glob, dir)) ?? false)
                {
                    yield break;
                }
                var directoryContents = fileProvider.GetDirectoryContents(dir).ToList();
                foreach (var file in directoryContents.Where(x => !x.IsDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string filePath = Path.Join(dir, file.Name);
                    if (config.Exclude?.Any(glob => pathMatcher.Matches(glob, filePath)) ?? false)
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
                    foreach (var file in ReadDir(Path.Join(dir, subDir.Name)))
                    {
                        yield return file;
                    }
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

                    if (songFileInfo.Exists)
                    {
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
                    }
                    else
                    {
                        _logger.LogWarning("Missing song {SourcePath} referenced in playlist {PlaylistPath}", sourcePath, playlist.PlaylistFileInfo.Path);
                    }
                    i++;
                }
            }
            catch (Exception ex)
            {
                songOutput.Fault(ex);
                throw;
            }
        }

        private async Task<OutputFile?> ConvertPlaylist(Playlist playlist, SyncConfig syncConfig, ISyncTarget syncTarget, PathComparer pathComparer, ConcurrentBag<FileSourceTargetInfo> handledFiles, CancellationToken handledFilesCompleteToken, CancellationToken cancellationToken)
        {
            var playlistFile = playlist.PlaylistFileInfo;

            var playlistPath = _pathTransformer.TransformPath(playlistFile.Path, PathTransformType.FilePath, syncConfig.DeviceConfig, out _);

            var playlistTargetSongs = new List<PlaylistSong>();
            var allSongsFound = true;
            foreach (var song in playlist.Songs)
            {
                if (Path.IsPathRooted(song.Path))
                    throw new InvalidOperationException($"Playlist song paths must not be absolute! In {playlist.PlaylistFileInfo.Path}");

                string? playlistDirectoryPath = Path.GetDirectoryName(playlistFile.Path);
                if (playlistDirectoryPath == null)
                    throw new ArgumentException($"Playlist path {playlistDirectoryPath} is invalid");
                var songPath = Path.Join(playlistDirectoryPath, song.Path);

                string? targetFilePath = null;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var handledFilesComplete = handledFilesCompleteToken.IsCancellationRequested;
                    var songMapping = handledFiles.FirstOrDefault(x => pathComparer.Equals(x.SourcePath, songPath));
                    if (songMapping != null)
                    {
                        targetFilePath = songMapping.TargetPath;
                        break;
                    }
                    if (handledFilesComplete)
                    {
                        _logger.LogWarning("Missing song {SourcePath} referenced in playlist {PlaylistPath}", songPath, playlistFile.Path);
                        allSongsFound = false;
                        break;
                    }
                    await Task.Delay(1000, cancellationToken);
                }
                if (targetFilePath == null)
                    continue;

                string songName = song.Name ?? Path.GetFileNameWithoutExtension(song.Path);
                playlistTargetSongs.Add(new PlaylistSong(PathUtils.MakeUnixPath(Path.GetRelativePath(playlistDirectoryPath, targetFilePath)), _sanitizer.SanitizeText(syncConfig.DeviceConfig.TagCharacterLimitations, songName, out _)));
            }

            var targetPlaylistFileInfo = await syncTarget.GetFileInfo(playlistPath, cancellationToken);
            if (allSongsFound && targetPlaylistFileInfo != null && FileDatesEqual(playlistFile.ModifiedDate, targetPlaylistFileInfo.LastModified))
            {
                handledFiles.Add(new FileSourceTargetInfo(playlistFile.Path, playlistPath));
                return null;
            }

            var tmpFilePath = _tempFileSession.GetTempFilePath(".m3u");

            using (var sw = File.CreateText(tmpFilePath))
            {
                sw.NewLine = "\r\n";
                await _playlistWriter.WriteM3u(sw, playlistTargetSongs);
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

        private SongSyncInfo? GetSyncInfo(SourceFileInfo sourceFile, SyncConfig config)
        {
            if (!config.SourceExtensions.Contains(Path.GetExtension(sourceFile.Path), StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            return new SongSyncInfo
            {
                SourceFileInfo = sourceFile,
                TargetPath = sourceFile.Path
            };
        }

        private async IAsyncEnumerable<SongReadWorkItem> CompareSongDates(SongSyncInfo[] syncInfos, SyncConfig config, PathComparer pathComparer, ISyncTarget syncTarget, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (syncInfos.Length == 0)
                yield break;

            var proposedDirPath = Path.GetDirectoryName(syncInfos[0].TargetPath) ?? throw new ArgumentException("DirectoryName is null");

            Debug.Assert(syncInfos.All(x => pathComparer.Equals(Path.GetDirectoryName(x.TargetPath), proposedDirPath)), "CompareSongDates can only compare batches of files in the same directory");

            var duplicateFiles = syncInfos.GroupBy(x => new NormalizedPath(Path.GetFileNameWithoutExtension(x.TargetPath)), pathComparer).Where(group => group.Count() > 1).ToList();
            if (duplicateFiles.Any())
            {
                foreach (var duplicateFile in duplicateFiles)
                {
                    _logger.LogError("Filenames {Filenames} result in a collision in the target path.", string.Join(", ", duplicateFile.Select(x => $"'{x.TargetPath}'")));
                }
            }

            var targetDirPath = _pathTransformer.TransformPath(proposedDirPath, PathTransformType.DirPath, config.DeviceConfig, out _);

            var directoryContents = await syncTarget.GetDirectoryContents(targetDirPath, cancellationToken);

            foreach (var syncInfo in syncInfos)
            {
                SongReadWorkItem? result = null;
                using (StartLogContext(syncInfo.SourceFileInfo, syncInfo.TargetPath))
                {
                    var targetPath = _pathTransformer.TransformPath(syncInfo.TargetPath, PathTransformType.FilePath, config.DeviceConfig, out var hasUnsupportedChars);

                    var targetInfos = directoryContents?.Where(x => pathComparer.FileNameEquals(Path.GetFileNameWithoutExtension(targetPath), Path.GetFileNameWithoutExtension(x.Name))).ToArray() ?? Array.Empty<SyncTargetFileInfo>();

                    if (targetInfos.Length == 1 && FileDatesEqual(syncInfo.SourceFileInfo.ModifiedDate, targetInfos[0].LastModified))
                    {
                        // only one target item, so check if it is up to date
                        result = new SongReadWorkItem
                        {
                            ActionType = CompareResultType.Keep,
                            SourceFileInfo = syncInfo.SourceFileInfo,
                            TargetFilePath = Path.Join(targetDirPath, targetInfos[0].Name)
                        };
                    }
                    else
                    {
                        if (hasUnsupportedChars)
                            _logger.LogInformation("Unsupported chars in path");

                        // zero or multiple target files, so just replace it
                        result = new SongReadWorkItem
                        {
                            ActionType = CompareResultType.Replace,
                            SourceFileInfo = syncInfo.SourceFileInfo,
                            TargetFilePath = targetPath
                        };
                    }
                }
                if (result != null)
                    yield return result;
            }
        }

        private bool FileDatesEqual(DateTimeOffset source, DateTimeOffset target)
        {
            if ((source.UtcDateTime - target.UtcDateTime).Duration() <= _fileTimestampDelta ||
                (source.UtcDateTime - target.UtcDateTime - TimeSpan.FromHours(1)).Duration() <= _fileTimestampDelta ||
                (source.UtcDateTime - target.UtcDateTime + TimeSpan.FromHours(1)).Duration() <= _fileTimestampDelta)
                return true;

            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                // we are on windows, check for files synced on linux (which uses UTC FAT32) -> check for source UTC == target local
                if ((source.UtcDateTime - target.LocalDateTime).Duration() <= _fileTimestampDelta ||
                    (source.UtcDateTime - target.LocalDateTime - TimeSpan.FromHours(1)).Duration() <= _fileTimestampDelta ||
                    (source.UtcDateTime - target.LocalDateTime + TimeSpan.FromHours(1)).Duration() <= _fileTimestampDelta)
                    return true;
            } else if (Environment.OSVersion.Platform == PlatformID.Unix) {
                // we are on unix, check for files synced on windows (which uses local FAT32) -> check for source local == target UTC
                if ((source.LocalDateTime - target.UtcDateTime).Duration() <= _fileTimestampDelta ||
                    (source.LocalDateTime - target.UtcDateTime - TimeSpan.FromHours(1)).Duration() <= _fileTimestampDelta ||
                    (source.LocalDateTime - target.UtcDateTime + TimeSpan.FromHours(1)).Duration() <= _fileTimestampDelta)
                    return true;
            }

            return false;
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
                    var sw = Stopwatch.StartNew();
                    long fileSize = 0;
                    string tmpFilePath;
                    using (var inFile = fileProvider.GetFileInfo(workItem.SourceFileInfo.Path).CreateReadStream())
                    {
                        (tmpFilePath, fileSize) = await _tempFileSession.CopyToTempFile(inFile, Path.GetExtension(workItem.SourceFileInfo.Path), cancellationToken);
                    }
                    sw.Stop();

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
                    Console.WriteLine($"<-- Read ({(fileSize / sw.Elapsed.TotalSeconds / 1024 / 1024).ToString("F2", CultureInfo.InvariantCulture)}MiB/s) {workItem.SourceFileInfo.Path}");
                    break;
                default:
                    throw new ArgumentException("Invalid ReadActionType");
            }
            return toReturn;
        }

        private async Task<string?> GetAlbumCoverPath(string dirPath, IFileProvider fileProvider, CancellationToken cancellationToken)
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
                            var (path, _) = await _tempFileSession.CopyToTempFile(coverStream, Path.GetExtension(coverVariant), cancellationToken);
                            return path;
                        }
                    }
                }
            }
            return null;
        }

        private async Task<OutputFile?> ConvertSong(PathMatcher pathMatcher, SongConvertWorkItem workItem, SyncConfig config, ConcurrentBag<FileSourceTargetInfo> handledFiles, CancellationToken cancellationToken)
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

                            string outFile;
                            string outputExtension;
                            try
                            {
                                (outFile, outputExtension) = await _converter.RemuxOrConvert(pathMatcher, config, workItem.SourceTempFilePath, workItem.SourceFileInfo.Path, workItem.AlbumArtPath, cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            var outFilePath = Path.ChangeExtension(workItem.TargetFilePath, outputExtension);

                            handledFiles.Add(new FileSourceTargetInfo(workItem.SourceFileInfo.Path, outFilePath));
                            sw.Stop();
                            Console.WriteLine($"<-- Convert ({sw.ElapsedMilliseconds}ms) {workItem.SourceFileInfo.Path}");

                            return new OutputFile
                            {
                                ModifiedDate = workItem.SourceFileInfo.ModifiedDate,
                                TempFilePath = outFile,
                                Path = outFilePath
                            };
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

        private async Task DeleteAdditional(ISyncTarget syncTarget, IEnumerable<FileSourceTargetInfo> handledFiles, PathComparer pathComparer, PathMatcher pathMatcher, IList<string>? keepPatterns, CancellationToken cancellationToken)
        {
            var normalizedHandledFiles = handledFiles.Select(x => new NormalizedPath(x.TargetPath)).ToHashSet();

            var (allToDelete, _) = await GetToDelete("");
            foreach (var x in allToDelete)
            {
                Console.WriteLine($"Delete {x.Path}");
            }
            await syncTarget.Delete(allToDelete, cancellationToken);

            async Task<(List<SyncTargetFileInfo> ToDelete, bool IsEmpty)> GetToDelete(string path)
            {
                var content = (await syncTarget.GetDirectoryContents(path, cancellationToken)) ?? throw new ArgumentException($"missing directory {path}");
                var leftoverContent = new HashSet<SyncTargetFileInfo>(content);
                var toDelete = new List<SyncTargetFileInfo>();
                foreach (var item in content)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string itemPath = Path.Join(path, item.Name);
                    if (await syncTarget.IsHidden(itemPath) || (keepPatterns?.Any(x => pathMatcher.Matches(x, itemPath)) ?? false))
                        continue;
                    if (item.IsDirectory)
                    {
                        var (subItemsToDelete, subDirIsEmpty) = await GetToDelete(itemPath);
                        toDelete.AddRange(subItemsToDelete);
                        if (subDirIsEmpty)
                        {
                            leftoverContent.Remove(item);
                            toDelete.Add(item);
                        }
                    }
                    else
                    {
                        if (!normalizedHandledFiles.Contains(new NormalizedPath(itemPath), pathComparer))
                        {
                            leftoverContent.Remove(item);
                            toDelete.Add(item);
                        }
                    }
                }
                return (toDelete, !leftoverContent.Any());
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
