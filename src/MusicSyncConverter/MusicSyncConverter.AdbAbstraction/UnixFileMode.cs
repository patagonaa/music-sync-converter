using System;

namespace MusicSyncConverter.AdbAbstraction
{
    [Flags]
    // https://man7.org/linux/man-pages/man7/inode.7.html st_mode
    public enum UnixFileMode
    {
        //FileTypeMask = 0xF000,
        Socket = 0xC000,
        SymLink = 0xA000,
        RegularFile = 0x8000,
        BlockDevice = 0x6000,
        Directory = 0x4000,
        CharacterDevice = 0x2000,
        Fifo = 0x1000,

        SetUid = 0x0800,
        SetGid = 0x0400,
        Sticky = 0x0200,

        //OwnerPermissionsMask = 0x01C0,
        OwnerRead = 0x0100,
        OwnerWrite = 0x0080,
        OwnerExecute = 0x0040,

        //GroupPermissionsMask = 0x0038,
        GroupRead = 0x0020,
        GroupWrite = 0x0010,
        GroupExecute = 0x0008,

        //OthersPermissionMask = 0x0007,
        OthersRead = 0x0004,
        OthersWrite = 0x0002,
        OthersExecute = 0x0001,

        //S_IFMT     0170000   bit mask for the file type bit fields
        //S_IFSOCK   0140000   socket
        //S_IFLNK    0120000   symbolic link
        //S_IFREG    0100000   regular file
        //S_IFBLK    0060000   block device
        //S_IFDIR    0040000   directory
        //S_IFCHR    0020000   character device
        //S_IFIFO    0010000   FIFO
        //S_ISUID    0004000   set UID bit
        //S_ISGID    0002000   set-group-ID bit (see below)
        //S_ISVTX    0001000   sticky bit (see below)
        //S_IRWXU    00700     mask for file owner permissions
        //S_IRUSR    00400     owner has read permission
        //S_IWUSR    00200     owner has write permission
        //S_IXUSR    00100     owner has execute permission
        //S_IRWXG    00070     mask for group permissions
        //S_IRGRP    00040     group has read permission
        //S_IWGRP    00020     group has write permission
        //S_IXGRP    00010     group has execute permission
        //S_IRWXO    00007     mask for permissions for others (not in group)
        //S_IROTH    00004     others have read permission
        //S_IWOTH    00002     others have write permission
        //S_IXOTH    00001     others have execute permission
    }
}
