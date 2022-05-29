using System;
using System.Text;

namespace MusicSyncConverter.Config
{
    public class CharReplacement
    {
        public string? Char
        {
            get
            {
                return Rune.ToString();
            }
            set
            {
                if (value == null || value.Length == 0)
                {
                    throw new ArgumentException("Missing char", nameof(value));
                }
                else if (value.Length == 1)
                {
                    Rune = new Rune(value[0]);
                }
                else if (value.Length == 2)
                {
                    Rune = new Rune(value[0], value[1]);
                }
                else
                {
                    throw new ArgumentException($"Invalid Char {value}");
                }
            }
        }
        public Rune Rune { get; private set; }
        public string? Replacement { get; set; }
    }
}