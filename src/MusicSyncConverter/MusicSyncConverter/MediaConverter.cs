using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Enums;
using MusicSyncConverter.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter
{
    class MediaConverter
    {
        public async Task<string> Convert(string sourcePath, EncoderInfo encoderInfo, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken)
        {
            var outFilePath = TempFileHelper.GetTempFilePath();
            var args = FFMpegArguments
                .FromFileInput(sourcePath)
                .OutputToFile(outFilePath, true, x => // we do not use pipe output here because ffmpeg can't write the header correctly when we use streams
                {
                    x.WithAudioCodec(encoderInfo.Codec);

                    if (encoderInfo.Bitrate.HasValue)
                    {
                        x.WithAudioBitrate(encoderInfo.Bitrate.Value);
                    }

                    if (!string.IsNullOrEmpty(encoderInfo.Profile))
                    {
                        x.WithArgument(new CustomArgument($"-profile:a {encoderInfo.Profile}"));
                    }
                    x.WithoutMetadata();
                    foreach (var tag in tags)
                    {
                        x.WithArgument(new CustomArgument($"-metadata {tag.Key}=\"{tag.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""));
                    }

                    if (!string.IsNullOrEmpty(encoderInfo.CoverCodec))
                    {
                        x.WithVideoCodec(encoderInfo.CoverCodec);
                        if (encoderInfo.MaxCoverSize != null)
                        {
                            x.WithArgument(new CustomArgument($"-vf \"scale='min({encoderInfo.MaxCoverSize.Value},iw)':min'({encoderInfo.MaxCoverSize.Value},ih)':force_original_aspect_ratio=decrease\""));
                        }
                    }
                    else
                    {
                        x.DisableChannel(Channel.Video);
                    }

                    x.ForceFormat(encoderInfo.Muxer);

                    if (!string.IsNullOrEmpty(encoderInfo.AdditionalFlags))
                    {
                        x.WithArgument(new CustomArgument(encoderInfo.AdditionalFlags));
                    }
                })
                .CancellableThrough(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await args
                //.NotifyOnProgress(x => Console.WriteLine($"{workItem.SourceFileInfo.AbsolutePath} {x.ToString()}"))
                .ProcessAsynchronously();
            cancellationToken.ThrowIfCancellationRequested();
            return outFilePath;
        }
    }
}
