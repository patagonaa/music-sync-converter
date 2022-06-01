namespace MusicSyncConverter.Models
{
    public class SongSyncInfo
    {
        public SourceFileInfo SourceFileInfo { get; set; } = null!;
        /// <summary>
        /// Proposed target path.
        /// The final target path depends on the final file extension and the supported characters,
        /// e.g. "Audio/𝕎𝕖𝕚𝕣𝕕 𝕌𝕟𝕚𝕔𝕠𝕕𝕖/Test.mp3" might be turned into "Audio/Weird Unicode/Test.m4a"
        /// </summary>
        public string TargetPath { get; set; } = null!;
    }
}