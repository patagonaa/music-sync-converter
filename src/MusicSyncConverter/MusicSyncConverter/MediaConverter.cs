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
        public async Task<string> Convert(string sourcePath, string coverPath, EncoderInfo encoderInfo, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken)
        {
            var outFilePath = TempFileHelper.GetTempFilePath();
            var args = FFMpegArguments
                .FromFileInput(sourcePath);
            var hasExternalCover = encoderInfo.CoverCodec != null && coverPath != null;
            if (hasExternalCover)
            {
                args.AddFileInput(coverPath);
            }

            var argsProcessor = args.OutputToFile(outFilePath, true, x => // we do not use pipe output here because ffmpeg can't write the header correctly when we use streams
            {
                x.WithArgument(new CustomArgument("-map 0:a"));
                if (hasExternalCover)
                {
                    x.WithArgument(new CustomArgument("-map 1:v -disposition:v attached_pic"));
                }
                else
                {
                    x.WithArgument(new CustomArgument("-map 0:v? -disposition:v attached_pic"));
                }

                x.WithAudioCodec(encoderInfo.Codec);

                if (encoderInfo.Channels.HasValue)
                {
                    x.WithArgument(new CustomArgument($"-ac {encoderInfo.Channels.Value}"));
                }

                if (encoderInfo.SampleRateHz.HasValue)
                {
                    x.WithAudioSamplingRate(encoderInfo.SampleRateHz.Value);
                }

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
            });
            argsProcessor.CancellableThrough(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await argsProcessor
                //.NotifyOnProgress(x => Console.WriteLine($"{workItem.SourceFileInfo.AbsolutePath} {x.ToString()}"))
                .ProcessAsynchronously();
            cancellationToken.ThrowIfCancellationRequested();
            return outFilePath;
        }
    }
}
