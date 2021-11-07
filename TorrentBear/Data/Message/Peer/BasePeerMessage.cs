using System;
using System.Linq;

namespace TorrentBear.Data.Message.Peer
{
    public class BasePeerMessage
    {
        public virtual int Length { get; set; }
        public virtual byte Type { get; set; }

        public virtual byte[] GetBytes()
        {
            var bytes = BitConverter.GetBytes(Length).ToList();
            bytes.Add(Type);
            return bytes.ToArray();
        }
    }
}