using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Virtuelizacija.Analytics
{
    public class WarningEventArgs : EventArgs
    {
        public WarningType Type { get; }
        public string Message { get; }
        public int RowIndex { get; }

        public WarningEventArgs(WarningType type, string message, int rowIndex)
        {
            Type = type;
            Message = message;
            RowIndex = rowIndex;
        }
    }
}
