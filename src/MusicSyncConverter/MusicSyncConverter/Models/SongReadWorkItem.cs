namespace MusicSyncConverter.Models
{
    public class SongReadWorkItem
    {
        public CompareResultType ActionType { get; set; }
        public SourceFileInfo SourceFileInfo { get; set; } = null!;
        /// <summary>
        /// If <see cref="ActionType"/> is <see cref="CompareResultType.Keep"/>, this is the actual path a file already is.
        /// If <see cref="ActionType"/> is <see cref="CompareResultType.Replace"/> this is the path where the file is supposed to go (with a temporary file extension)
        /// </summary>
        public string TargetFilePath { get; set; } = null!;
    }
}
