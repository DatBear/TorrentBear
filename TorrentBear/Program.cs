using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BencodeNET.Parsing;
using BencodeNET.Torrents;
using TorrentBear.Service;

namespace TorrentBear
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var seederTask = Task.Run(() => new Program().Seeder());
            var leecherTask = Task.Run(() => new Program().Leecher());
            var leecher2Task = Task.Run(() => new Program().Leecher2());
            await Task.WhenAll(seederTask, leecherTask, leecher2Task);
            var seeder = await seederTask;
            var leecher = await leecherTask;
            var leecher2 = await leecher2Task;
            Console.ReadLine();
        }

        void Run()
        {
            var parser = new BencodeParser();
            var torrent = parser.Parse<Torrent>("./Torrent/1.torrent");
            Console.ReadLine();
        }

        private int _seedPort = 4000;
        private int _leechPort = 4001;

        TorrentDownloader Seeder()
        {
            var peers = new List<IPEndPoint>();
            var downloader = new TorrentDownloader("seed", _seedPort, "./Torrent/1.torrent", "./Torrent/1/");
            Thread.Sleep(3000);
            downloader.Start(peers);
            return downloader;
        }

        TorrentDownloader Leecher()
        {
            var peers = new List<IPEndPoint>
            {
                new(IPAddress.Loopback, _seedPort),
                new(IPAddress.Loopback, _leechPort+2)
            };
            var downloader = new TorrentDownloader("leech1", _leechPort, "./Torrent/1.torrent", "./leeched/1/");
            Thread.Sleep(3000);
            downloader.Start(peers);
            return downloader;
        }

        TorrentDownloader Leecher2()
        {
            var peers = new List<IPEndPoint>
            {
                new(IPAddress.Loopback, _seedPort),
            };
            var peer = new TorrentDownloader("leech2", _leechPort + 2, "./Torrent/1.torrent", "./leeched/2/");
            Thread.Sleep(3000);
            peer.Start(peers);
            return peer;
        }
    }
}
