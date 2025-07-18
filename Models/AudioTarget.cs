﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeejNG.Models
{
    public class AudioTarget
    {
        public string Name { get; set; } = "";
        public bool IsInputDevice { get; set; } = false;
        public bool IsOutputDevice { get; set; } = false;

        public override string ToString() => Name;

        public override bool Equals(object obj)
        {
            if (obj is AudioTarget other)
                return string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) &&
                       IsInputDevice == other.IsInputDevice &&
                       IsOutputDevice == other.IsOutputDevice;
            return false;
        }

        public override int GetHashCode()
        {
            return (Name?.ToLowerInvariant()?.GetHashCode() ?? 0) ^ IsInputDevice.GetHashCode() ^ IsOutputDevice.GetHashCode();
        }
    }
}