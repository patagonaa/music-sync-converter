using System.Buffers.Binary;
using System.Text;
using System;

namespace MusicSyncConverter.Tags
{
    internal readonly record struct AlbumArt(ApicType Type, string MimeType, string? Description, byte[] PictureData)
    {
        public string ToVorbisMetaDataBlockPicture()
        {
            var toReturn = new byte[8 + MimeType.Length + 4 + (Description?.Length ?? 0) + 20 + PictureData.Length];
            var span = toReturn.AsSpan();

            var i = 0;

            BinaryPrimitives.WriteUInt32BigEndian(span[i..], (uint)Type);
            i += 4;

            var mimeTypeBytes = Encoding.ASCII.GetBytes(MimeType);
            BinaryPrimitives.WriteUInt32BigEndian(span[i..], (uint)mimeTypeBytes.Length);
            i += 4;

            Array.Copy(mimeTypeBytes, 0, toReturn, i, mimeTypeBytes.Length);
            i += mimeTypeBytes.Length;

            if (Description != null)
            {
                var descriptionBytes = Encoding.UTF8.GetBytes(Description);
                BinaryPrimitives.WriteUInt32BigEndian(span[i..], (uint)descriptionBytes.Length);
                i += 4;

                Array.Copy(descriptionBytes, 0, toReturn, i, descriptionBytes.Length);
                i += descriptionBytes.Length;
            }
            else
            {
                i += 4; // description length
            }

            i += 4; // width = 0 (ignore)
            i += 4; // height = 0 (ignore)
            i += 4; // color depth = 0 (ignore)
            i += 4; // color count = 0 (ignore)

            BinaryPrimitives.WriteUInt32BigEndian(span[i..], (uint)PictureData.Length);
            i += 4;
            Array.Copy(PictureData, 0, toReturn, i, PictureData.Length);
            i += PictureData.Length;

            if (i != toReturn.Length)
            {
                throw new InvalidOperationException("Wrong MetaData Length");
            }

            return Convert.ToBase64String(toReturn);
        }
    }
}
