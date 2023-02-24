using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CardGameUtils;

namespace CardGameServer;

class Room
{
	public struct Player
	{
		public string? Name { get; set; }
		public string[]? Decklist { get; set; }
		public string ID { get; set; }
		public bool ready;
		public bool noshuffle;
	}
	public int port;
	public Player[] players = new Player[2];
	public Process? core;
	public Room(string name, string id, int port)
	{
		this.port = port;
		players[0] = new Player() { Name = name, ready = false, ID = id, noshuffle = false };
		Functions.Log("Creating a new room. Host name: " + name, includeFullPath: true);
	}
	public string CardNameToFilename(string name)
	{
		return Regex.Replace(name, @"[^#\|a-zA-Z0-9]", "");
	}
	public string? GeneratePlayerString()
	{
		string s = "µ" + players[0].Name + "µ" + players[0].ID + "µ";
		if (players[0].Decklist == null)
		{
			Functions.Log($"Unable to generate player string, player 0 ({players[0].Name}) has no decklist", severity: Functions.LogSeverity.Error, includeFullPath: true);
			return null;
		}
		foreach (string name in players[0].Decklist!)
		{
			if (s != "")
				s += CardNameToFilename(name) + ";";
		}
		s = s.Remove(s.Length - 1, 1);
		s += "µ" + players[1].Name + "µ" + players[1].ID + "µ";
		if (players[1].Decklist == null)
		{
			Functions.Log($"Unable to generate player string, player 1 ({players[1].Name}) has no decklist", severity: Functions.LogSeverity.Error, includeFullPath: true);
			return null;
		}
		foreach (string name in players[1].Decklist!)
		{
			if (s != "")
				s += CardNameToFilename(name) + ";";
		}
		s = s.Remove(s.Length - 1, 1);
		s += "µ";
		return s;
	}
	public bool StartGame()
	{
		if (!players[0].ready || !players[1].ready || core != null)
		{
			Functions.Log("StartGame() was called for a not ready room", Functions.LogSeverity.Warning, includeFullPath: true);
			return false;
		}
		string? playerString = GeneratePlayerString();
		if (playerString == null)
		{
			return false;
		}
		string additionalArgs = $" --port={port} --players={Convert.ToBase64String(Encoding.UTF8.GetBytes(playerString))}" +
			$" --noshuffle={players[0].noshuffle && players[1].noshuffle}";
		ProcessStartInfo info = new ProcessStartInfo
		{
			Arguments = Program.config.core_info.Arguments + additionalArgs,
			CreateNoWindow = Program.config.core_info.CreateNoWindow,
			UseShellExecute = Program.config.core_info.UseShellExecute,
			FileName = Program.config.core_info.FileName,
			WorkingDirectory = Program.config.core_info.WorkingDirectory,
		};
		core = Process.Start(info);
		if (core == null)
		{
			Functions.Log("Could not start the core process", severity: Functions.LogSeverity.Error, includeFullPath: true);
			return false;
		}
		core.Exited += (sender, e) =>
		{
			Functions.Log("Removing finished room");
			Program.runningList.Remove(this);
		};
		return true;
	}
}