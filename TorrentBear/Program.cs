using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BencodeNET.Parsing;
using BencodeNET.Torrents;
using TorrentBear.Enum;
using TorrentBear.Service;

namespace TorrentBear
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var torrentFilePath = "./Torrent/1.torrent";
            var seedDownloadPath = "./Torrent/1/";
            var seederTasks = new List<Task<TorrentDownloader>>();
            var leecherTasks = new List<Task<TorrentDownloader>>();
            var peers = new List<IPEndPoint>();
            for (var i = 0; i < 1; i++)
            {
                var idx = i;
                var task = Task.Run(() => new Program().Downloader($"seed {idx + 1}", _seedPort + idx, torrentFilePath, seedDownloadPath, new List<IPEndPoint>()));
                seederTasks.Add(task);
                peers.Add(new IPEndPoint(IPAddress.Loopback, _seedPort + idx));
            }

            for (var i = 0; i < 2; i++)
            {
                var idx = i;
                var task = Task.Run(() =>
                    new Program().Downloader($"leech{idx + 1}", _leechPort + idx, torrentFilePath, $"./leeched/{idx + 1}/", peers.ToList()));
                leecherTasks.Add(task);
                peers.Add(new IPEndPoint(IPAddress.Loopback, _leechPort + idx));
            }

            await Task.WhenAll(seederTasks.Concat(leecherTasks));
            Console.ReadLine();
        }

        void Run()
        {
            var parser = new BencodeParser();
            var torrent = parser.Parse<Torrent>("./Torrent/1.torrent");
            Console.ReadLine();
        }

        private static int _seedPort = 4000;
        private static int _leechPort = 4100;

        TorrentDownloader Downloader(string name, int port, string torrentPath, string downloadPath, List<IPEndPoint> peers)
        {
            var downloader = new TorrentDownloader(name, port, torrentPath, downloadPath, BitfieldType.Full);
            Thread.Sleep(3000);
            downloader.Start(peers);
            return downloader;
        }
        
    }
}