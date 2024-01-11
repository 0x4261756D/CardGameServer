using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using CardGameUtils;
using CardGameUtils.Structs;
using static CardGameUtils.Structs.NetworkingStructs;

namespace CardGameServer;

partial class Room
{
	public struct Player
	{
		public string? Name { get; set; }
		public string[]? Decklist { get; set; }
		public string ID { get; set; }
		public bool ready;
		public bool noshuffle;
		public NetworkStream stream;
	}
	public int port;
	public Player[] players = new Player[2];
	public Process? core;
	public DateTime startTime;
	public Room(string name, string id, int port, NetworkStream stream)
	{
		this.port = port;
		players[0] = new Player() { Name = name, ready = false, ID = id, noshuffle = false, stream = stream };
		Functions.Log("Creating a new room. Host name: " + name, includeFullPath: true);
		startTime = DateTime.Now;
	}
	public string? GeneratePlayerString()
	{
		CoreConfig.PlayerConfig[] infos = new CoreConfig.PlayerConfig[players.Length];
		for(int i = 0; i < players.Length; i++)
		{
			if(players[i].Name == null)
			{
				Functions.Log($"Unable to generate player string, player {i} has no name", severity: Functions.LogSeverity.Error, includeFullPath: true);
				return null;
			}
			if(players[i].Decklist == null)
			{
				Functions.Log($"Unable to generate player string, player {i} ({players[i].Name}) has no decklist", severity: Functions.LogSeverity.Error, includeFullPath: true);
				return null;
			}
			infos[i] = new CoreConfig.PlayerConfig(name: players[i].Name!, id: players[i].ID, decklist: players[i].Decklist!);
		}
		return JsonSerializer.Serialize(infos, options: NetworkingConstants.jsonIncludeOption);
	}
	public bool StartGame()
	{
		if(!players[0].ready || !players[1].ready || core != null)
		{
			Functions.Log("StartGame() was called for a not ready room", Functions.LogSeverity.Warning, includeFullPath: true);
			return false;
		}
		string? playerString = GeneratePlayerString();
		if(playerString == null)
		{
			return false;
		}
		using AnonymousPipeServerStream pipeServerStream = new(PipeDirection.In, HandleInheritability.Inheritable);
		string additionalArgs = $" --replay=true --mode=duel --port={port} --players={Convert.ToBase64String(Encoding.UTF8.GetBytes(playerString))}" +
			$" --noshuffle={players[0].noshuffle && players[1].noshuffle} --pipe={pipeServerStream.GetClientHandleAsString()}";
		ProcessStartInfo info = new()
		{
			Arguments = Program.config.core_info.Arguments + additionalArgs,
			CreateNoWindow = Program.config.core_info.CreateNoWindow,
			UseShellExecute = Program.config.core_info.UseShellExecute,
			FileName = Program.config.core_info.FileName,
			WorkingDirectory = Program.config.core_info.WorkingDirectory,
		};
		core = Process.Start(info);
		if(core == null)
		{
			Functions.Log("Could not start the core process", severity: Functions.LogSeverity.Error, includeFullPath: true);
			return false;
		}
		core.Exited += (sender, e) =>
		{
			Functions.Log("Removing finished room");
			Program.runningList.Remove(this);
		};
		using StreamReader reader = new(pipeServerStream);
		Functions.Log("reading", severity: Functions.LogSeverity.Warning);
		while(reader.Read() != 42)
		{
			Thread.Sleep(10);
		}
		Functions.Log("Done reading", severity: Functions.LogSeverity.Warning);
		foreach(Player player in players)
		{
			player.stream.Write(Functions.GeneratePayload(new ServerPackets.StartResponse
			{
				success = ServerPackets.StartResponse.Result.Success,
				id = player.ID,
				port = port,
			}));
			player.stream.Close();
			player.stream.Dispose();
		}
		return true;
	}
}
