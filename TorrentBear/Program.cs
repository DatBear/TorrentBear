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
            await Task.WhenAll(seederTask, leecherTask);
            var seeder = await seederTask;
            var leecher = await leecherTask;
            await seederTask;
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

        TorrentConnection Seeder()
        {
            var peers = new List<IPEndPoint>
            {
                new(IPAddress.Loopback, _leechPort)
            };
            var peer = new TorrentConnection("seed", _seedPort, peers, "./Torrent/1.torrent", "./Torrent/1/");
            Thread.Sleep(5000);
            peer.Start();
            return peer;
        }

        TorrentConnection Leecher()
        {
            var peers = new List<IPEndPoint>
            {
                new(IPAddress.Loopback, _seedPort)
            };
            var peer = new TorrentConnection("leech", _leechPort, peers, "./Torrent/1.torrent", "./downloaded/");
            Thread.Sleep(5000);
            peer.Start();
            return peer;
        }
    }
}
