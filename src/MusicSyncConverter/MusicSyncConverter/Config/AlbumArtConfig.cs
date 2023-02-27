namespace MusicSyncConverter.Config
{
    public class AlbumArtConfig
    {
        public string? Codec { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public ImageResizeType ResizeType { get; set; } = ImageResizeType.KeepInputAspectRatio;
    }
}