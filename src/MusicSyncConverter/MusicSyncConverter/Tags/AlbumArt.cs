namespace MusicSyncConverter.Tags
{
    internal readonly record struct AlbumArt(ApicType Type, string MimeType, string? Description, byte[] PictureData);
}
