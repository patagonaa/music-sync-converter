namespace MusicSyncConverter.Models
{
    public class SongConvertWorkItem
    {
        public ConvertActionType ActionType { get; set; }
        public SourceFileInfo SourceFileInfo { get; set; } = null!;
        public string SourceTempFilePath { get; set; } = null!;
        public string? AlbumArtPath { get; set; }
        /// <summary>
        /// If <see cref="ActionType"/> is <see cref="ConvertActionType.Keep"/>, this is the actual path a file already is.
        /// If <see cref="ActionType"/> is <see cref="ConvertActionType.RemuxOrConvert"/>, this is the path where the file is supposed to go (with a temporary file extension)
        /// </summary>
        public string TargetFilePath { get; set; } = null!;
    }
}
