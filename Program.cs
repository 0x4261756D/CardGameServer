using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CardGameUtils;
using CardGameUtils.Structs;
using static CardGameUtils.Structs.NetworkingStructs;

namespace CardGameServer;

class Program
{
	public static string baseDir = AppDomain.CurrentDomain.BaseDirectory;
	public static ServerConfig config = new ServerConfig("additional_cards/", 7043, 37042, 39942, new CoreInfo());
	public static DateTime lastAdditionalCardsTimestamp;
	public static string? seed;
	public static SHA384 sha = SHA384.Create();
	public static List<Room> waitingList = new List<Room>();
	public static List<Room> runningList = new List<Room>();
	static void Main(string[] args)
	{
		string? configLocation = null;
		for (int i = 0; i < args.Length; i++)
		{
			string[] parts = args[i].Split('=');
			if (parts.Length == 2)
			{
				switch (parts[0])
				{
					case "--config":
					case "-c":
						string path = Path.Combine(baseDir, parts[1]);
						if (File.Exists(path))
						{
							configLocation = path;
						}
						else
						{
							Functions.Log($"No config file found at {path}.", severity: Functions.LogSeverity.Error);
							return;
						}
						break;
				}
			}
		}
		if (configLocation == null)
		{
			Functions.Log("Please provide a config file with '--config=path/to/config'", severity: Functions.LogSeverity.Error);
			return;
		}
		PlatformServerConfig platformConfig = JsonSerializer.Deserialize<PlatformServerConfig>(File.ReadAllText(configLocation), NetworkingConstants.jsonIncludeOption);
		if (Environment.OSVersion.Platform == PlatformID.Unix)
		{
			config = platformConfig.linux;
		}
		else
		{
			config = platformConfig.windows;
		}
		if (File.Exists(config.additional_cards_path))
		{
			lastAdditionalCardsTimestamp = File.GetCreationTime(config.additional_cards_path);
		}
		TcpListener listener = new TcpListener(IPAddress.Any, config.port);
		byte[] nowBytes = Encoding.UTF8.GetBytes(DateTime.Now.ToString());
		seed = Convert.ToBase64String(sha.ComputeHash(nowBytes));
		listener.Start();
		List<byte> bytes = new List<byte>();
		while (true)
		{
			Functions.Log("Server waiting for a connection", includeFullPath: true);
			TcpClient client = listener.AcceptTcpClient();
			Functions.Log("Server connected", includeFullPath: true);
			using (NetworkStream stream = client.GetStream())
			{
				Functions.Log("Waiting for data", includeFullPath: true);
				while (!stream.DataAvailable)
				{
					Thread.Sleep(10);
				}
				bytes = Functions.ReceiveRawPacket(stream)!;
				Functions.Log("Server received a request", includeFullPath: true);
				if (HandlePacket(bytes, stream))
				{
					Functions.Log("Server received a request signalling it should stop", includeFullPath: true);
					break;
				}
				Functions.Log("Server sent a response", includeFullPath: true);
				stream.Close();
				client.Close();
			}
		}
		listener.Stop();

	}
	private static bool HandlePacket(List<byte> bytes, NetworkStream stream)
	{
		int cleanedRoomsCount = runningList.RemoveAll(x => x.core?.HasExited ?? false);
		Functions.Log($"Cleaned up {cleanedRoomsCount} abandoned rooms, {runningList.Count} rooms still open", includeFullPath: true);
		// THIS MIGHT CHANGE AS SENDING RAW JSON MIGHT BE TOO EXPENSIVE/SLOW
		byte type = bytes[0];
		bytes.RemoveAt(0);
		string packet = Encoding.UTF8.GetString(bytes.ToArray());
		List<byte> payload = new List<byte>();
		if (type >= NetworkingConstants.PACKET_COUNT)
		{
			throw new Exception($"ERROR: Unknown packet type encountered: Packet type: {NetworkingConstants.PacketTypeToName(type)} ({type}) | {packet}");
		}
		Functions.Log($"Received packet of type {type}", includeFullPath: true);
		if (type == NetworkingConstants.PACKET_SERVER_CREATE_REQUEST)
		{
			string name = Functions.DeserializeJson<ServerPackets.CreateRequest>(packet).name!;
			Predicate<Room> nameExists = x => x.players[0].Name == name || x.players[1].Name == name;
			if (name.Contains("µ"))
			{
				payload = Functions.GeneratePayload<ServerPackets.CreateResponse>(new ServerPackets.CreateResponse
				{
					success = false,
					reason = "Your name cannot contain 'µ'"
				});
			}
			else if (waitingList.Exists(nameExists) || runningList.Exists(nameExists))
			{
				payload = Functions.GeneratePayload<ServerPackets.CreateResponse>(new ServerPackets.CreateResponse
				{
					success = false,
					reason = "Oh oh, sorry kiddo, looks like someone else already has that name. Why don't you pick something else? (Please watch SAO Abridged if you don't get this reference)"
				});
			}
			else
			{
				string id = BitConverter.ToString(Program.sha.ComputeHash(Encoding.UTF8.GetBytes(Program.seed + name))).Replace("-", "");
				int currentPort = -1;
				for (int i = config.room_min_port; i <= config.room_max_port; i++)
				{
					bool free = true;
					foreach (Room r in waitingList)
					{
						if (r.port == i)
						{
							free = false;
							break;
						}
					}
					if (free)
					{
						foreach (Room r in runningList)
						{
							if (r.port == i)
							{
								free = false;
								break;
							}
						}
					}
					if (free)
					{
						Functions.Log($"Next port: {i}");
						currentPort = i;
						break;
					}
				}
				if (currentPort == -1)
				{
					Functions.Log("No free port found", severity: Functions.LogSeverity.Warning);
					payload = Functions.GeneratePayload<ServerPackets.CreateResponse>(new ServerPackets.CreateResponse
					{
						success = false,
						reason = "No free port found",
					});
				}
				else
				{
					waitingList.Add(new Room(name, id, currentPort));
					payload = Functions.GeneratePayload<ServerPackets.CreateResponse>(new ServerPackets.CreateResponse
					{
						success = true
					});
				}
			}
		}
		else if (type == NetworkingConstants.PACKET_SERVER_JOIN_REQUEST)
		{
			ServerPackets.JoinRequest request = Functions.DeserializeJson<ServerPackets.JoinRequest>(packet);
			Predicate<Room> nameExists = x => x.players[0].Name == request.name || x.players[1].Name == request.name;
			if (request.name!.Contains("µ"))
			{
				payload = Functions.GeneratePayload<ServerPackets.JoinResponse>(new ServerPackets.JoinResponse
				{
					success = false,
					reason = "Your name cannot contain 'µ'"
				});
			}
			else if (waitingList.FindIndex(nameExists) != -1 || runningList.FindIndex(nameExists) != -1)
			{
				payload = Functions.GeneratePayload<ServerPackets.JoinResponse>(new ServerPackets.JoinResponse
				{
					success = false,
					reason = "Oh oh, sorry kiddo, looks like someone else already has that name. Why don't you pick something else? (Please watch SAO Abridged if you don't get this reference)"
				});
			}
			else
			{
				int index = waitingList.FindIndex(x => x.players[0].Name == request.targetName);
				if (index == -1)
				{
					payload = Functions.GeneratePayload<ServerPackets.JoinResponse>(new ServerPackets.JoinResponse
					{
						success = false,
						reason = "No player with that name hosts a game right now"
					});
				}
				else
				{
					string id = BitConverter.ToString(Program.sha.ComputeHash(Encoding.UTF8.GetBytes(Program.seed + request.name))).Replace("-", "");
					waitingList[index].players[1] = new Room.Player { Name = request.name, ID = id };
					payload = Functions.GeneratePayload<ServerPackets.JoinResponse>(new ServerPackets.JoinResponse
					{
						success = true
					});
				}
			}
		}
		else if (type == NetworkingConstants.PACKET_SERVER_LEAVE_REQUEST)
		{
			string name = Functions.DeserializeJson<ServerPackets.LeaveRequest>(packet).name!;
			Predicate<Room> nameExists = x => x.players[0].Name == name || x.players[1].Name == name;
			int index = Program.waitingList.FindIndex(nameExists);
			if (index == -1)
			{
				payload = Functions.GeneratePayload<ServerPackets.LeaveResponse>(new ServerPackets.LeaveResponse
				{
					success = false,
					reason = "No player with that name found in a room"
				});
			}
			else
			{
				if (waitingList[index].players[0].Name == name)
				{
					waitingList[index].players[0].Name = null;
					waitingList[index].players[0].Decklist = null;
					waitingList[index].players[0].ready = false;
				}
				else
				{
					waitingList[index].players[1].Name = null;
					waitingList[index].players[1].Decklist = null;
					waitingList[index].players[1].ready = false;
				}
				if (waitingList[index].players[0].Name == null && waitingList[index].players[1].Name == null)
				{
					waitingList.RemoveAt(index);
				}
				payload = Functions.GeneratePayload<ServerPackets.LeaveResponse>(new ServerPackets.LeaveResponse
				{
					success = true
				});
			}
		}
		else if (type == NetworkingConstants.PACKET_SERVER_ROOMS_REQUEST)
		{
			if (waitingList.Exists(x => x.players[0].Name == null))
			{
				Functions.Log($"There is a player whose name is null", severity: Functions.LogSeverity.Error, includeFullPath: true);
				return false;
			}
			payload = Functions.GeneratePayload<ServerPackets.RoomsResponse>(new ServerPackets.RoomsResponse
			{
				rooms = waitingList.ConvertAll(x => x.players[0].Name!).ToArray()
			});
		}
		else if (type == NetworkingConstants.PACKET_SERVER_START_REQUEST)
		{
			Functions.Log("----START REQUEST HANDLING----", includeFullPath: true);
			ServerPackets.StartRequest request = Functions.DeserializeJson<ServerPackets.StartRequest>(packet);
			Predicate<Room> nameExists = x => x.players[0].Name == request.name || x.players[1].Name == request.name;
			int index = waitingList.FindIndex(nameExists);
			Functions.Log("Waiting List index: " + index, includeFullPath: true);
			if (index == -1)
			{
				// We still have to check if the other player started, moving the room to the running list
				index = runningList.FindIndex(nameExists);
				Functions.Log("Running List Index: " + index, includeFullPath: true);
				if (index == -1)
				{
					payload = Functions.GeneratePayload<ServerPackets.StartResponse>(new ServerPackets.StartResponse
					{
						success = false,
						reason = "You are not part of any waiting room"
					});
				}
				else
				{
					int player = (runningList[index].players[0].Name == request.name) ? 0 : 1;
					Functions.Log("Player: " + player, includeFullPath: true);
					runningList[index].players[player].ready = true;
					runningList[index].players[player].noshuffle = request.noshuffle;
					runningList[index].players[player].Decklist = request.decklist;
					if (runningList[index].StartGame())
					{
						Functions.Log("Starting the game", includeFullPath: true);
						payload = Functions.GeneratePayload<ServerPackets.StartResponse>(new ServerPackets.StartResponse
						{
							success = true,
							id = runningList[index].players.First(x => x.Name == request.name).ID,
							port = runningList[index].port,
						});

					}
					else
					{
						Functions.Log("Could not create the core", severity: Functions.LogSeverity.Error, includeFullPath: true);
						payload = Functions.GeneratePayload<ServerPackets.StartResponse>(new ServerPackets.StartResponse
						{
							success = false,
							reason = "Could not create a core"
						});
					}
				}
			}
			else
			{
				int player = (waitingList[index].players[0].Name == request.name) ? 0 : 1;
				Functions.Log("Player: " + player, includeFullPath: true);
				waitingList[index].players[player].ready = true;
				waitingList[index].players[player].noshuffle = request.noshuffle;
				waitingList[index].players[player].Decklist = request.decklist;
				if (waitingList[index].players[1 - player].Name == null)
				{
					Functions.Log("No opponent", includeFullPath: true);
					payload = Functions.GeneratePayload<ServerPackets.StartResponse>(new ServerPackets.StartResponse
					{
						success = false,
						reason = "You have no opponent",
					});
				}
				else
				{
					Functions.Log("Opponent present", includeFullPath: true);
					if (waitingList[index].players.All(x => x.ready))
					{
						Functions.Log("All players ready", includeFullPath: true);
						Room room = waitingList[index];
						runningList.Add(room);
						waitingList.RemoveAt(index);
						payload = Functions.GeneratePayload<ServerPackets.StartResponse>(new ServerPackets.StartResponse
						{
							success = true,
							// FIXME: make this nicer, perhaps send the id when creating it?
							id = room.players.First(x => x.Name == request.name).ID,
							// Other thought: Let the server be Man in the Middle between core and client
							port = room.port,
						});
					}
					else
					{
						Functions.Log("Opponent not ready", includeFullPath: true);
						payload = Functions.GeneratePayload<ServerPackets.StartResponse>(new ServerPackets.StartResponse
						{
							success = false,
							reason = "Your opponent isn't ready yet"
						});
					}
				}
			}
			Functions.Log("----END----", includeFullPath: true);
		}
		else if (type == NetworkingConstants.PACKET_SERVER_ADDITIONAL_CARDS_REQUEST)
		{
			if (!File.Exists(Program.config.additional_cards_path) ||
				File.GetLastWriteTime(Program.config.additional_cards_path) > Program.lastAdditionalCardsTimestamp)
			{
				ProcessStartInfo info = new ProcessStartInfo
				{
					Arguments = "--additional_cards_path=" + Program.config.additional_cards_path,
					CreateNoWindow = Program.config.core_info.CreateNoWindow,
					UseShellExecute = Program.config.core_info.UseShellExecute,
					FileName = Program.config.core_info.FileName,
					WorkingDirectory = Program.config.core_info.WorkingDirectory,
				};
				Process.Start(info)?.WaitForExit(10000);
			}

			if (File.Exists(config.additional_cards_path))
			{
				payload = Functions.GeneratePayload<ServerPackets.AdditionalCardsResponse>(
					JsonSerializer.Deserialize<ServerPackets.AdditionalCardsResponse>(File.ReadAllText(Program.config.additional_cards_path), NetworkingConstants.jsonIncludeOption)!);
			}
			else
			{
				payload = Functions.GeneratePayload<ServerPackets.AdditionalCardsResponse>(new ServerPackets.AdditionalCardsResponse());
			}
		}
		else
		{
			throw new Exception($"ERROR: Unable to process this packet: Packet type: {NetworkingConstants.PacketTypeToName(type)}({type}) | {packet}");
		}
		stream.Write(payload.ToArray(), 0, payload.Count);
		return false;
	}
}