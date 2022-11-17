using System;

namespace MusicSyncConverter.AdbAbstraction
{
    public class AdbException : Exception
    {
        public AdbException(string reason)
            : base(reason)
        {
        }
    }
}
