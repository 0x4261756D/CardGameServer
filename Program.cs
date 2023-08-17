using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
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
		for(int i = 0; i < args.Length; i++)
		{
			string[] parts = args[i].Split('=');
			if(parts.Length == 2)
			{
				switch(parts[0])
				{
					case "--config":
					case "-c":
						string path = Path.Combine(baseDir, parts[1]);
						if(File.Exists(path))
						{
							configLocation = path;
						}
						else
						{
							Functions.Log($"No config file found at {Path.GetFullPath(path)}.", severity: Functions.LogSeverity.Error);
							return;
						}
						break;
				}
			}
		}
		if(configLocation == null)
		{
			Functions.Log("Please provide a config file with '--config=path/to/config'", severity: Functions.LogSeverity.Error);
			return;
		}
		PlatformServerConfig platformConfig = JsonSerializer.Deserialize<PlatformServerConfig>(File.ReadAllText(configLocation), NetworkingConstants.jsonIncludeOption);
		if(Environment.OSVersion.Platform == PlatformID.Unix)
		{
			config = platformConfig.linux;
		}
		else
		{
			config = platformConfig.windows;
		}
		if(File.Exists(config.additional_cards_path))
		{
			lastAdditionalCardsTimestamp = File.GetCreationTime(config.additional_cards_path);
		}
		TcpListener listener = new TcpListener(IPAddress.Any, config.port);
		byte[] nowBytes = Encoding.UTF8.GetBytes(DateTime.Now.ToString());
		seed = Convert.ToBase64String(sha.ComputeHash(nowBytes));
		listener.Start();
		while(true)
		{
			Functions.Log("Server waiting for a connection", includeFullPath: true);
			TcpClient client = listener.AcceptTcpClient();
			Functions.Log("Server connected", includeFullPath: true);
			NetworkStream stream = client.GetStream();
			Functions.Log("Waiting for data", includeFullPath: true);
			HandlePacketReturn decision = HandlePacketReturn.Continue;
			try
			{
				(byte type, byte[]? bytes) = Functions.ReceiveRawPacket(stream);
				Functions.Log("Server received a request", includeFullPath: true);
				decision = HandlePacket(type, bytes, stream);
				if(decision == HandlePacketReturn.Break)
				{
					Functions.Log("Server received a request signalling it should stop", includeFullPath: true);
					break;
				}
				Functions.Log("Server sent a response", includeFullPath: true);
			}
			catch(Exception e)
			{
				Functions.Log($"Exception while reading a message: {e}");
			}
			if(decision != HandlePacketReturn.ContinueKeepStream)
			{
				stream.Close();
				client.Close();
				client.Dispose();
				stream.Dispose();
			}
		}
		listener.Stop();

	}

	private enum HandlePacketReturn
	{
		Break,
		Continue,
		ContinueKeepStream,
	}

	private static void CleanupRooms()
	{
		int runningCount = 0;
		for(int i = runningList.Count - 1; i >= 0; i--)
		{
			// TODO: Maybe don't be so mean, ask the players somehow if they are still alive?
			if((DateTime.Now - runningList[i].startTime).Days > 1 || (runningList[i].core?.HasExited ?? false))
			{
				runningList[i].players[0].stream.Close();
				runningList[i].players[1].stream.Close();
				runningList.RemoveAt(i);
				runningCount++;
			}
		}
		int waitingCount = 0;
		for(int i = waitingList.Count - 1; i >= 0; i--)
		{
			if((DateTime.Now - waitingList[i].startTime).Days > 1)
			{
				waitingList[i].players[0].stream.Close();
				waitingList[i].players[1].stream.Close();
				waitingList.RemoveAt(i);
				waitingCount++;
			}
		}
		Functions.Log($"Cleaned up {runningCount} abandoned running rooms, {runningList.Count} rooms still open", includeFullPath: true);
		Functions.Log($"Cleaned up {waitingCount} abandoned waiting rooms, {waitingList.Count} rooms still open", includeFullPath: true);
	}

	private static HandlePacketReturn HandlePacket(byte typeByte, byte[]? bytes, NetworkStream stream)
	{
		CleanupRooms();
		// THIS MIGHT CHANGE AS SENDING RAW JSON MIGHT BE TOO EXPENSIVE/SLOW
		if(typeByte >= (byte)NetworkingConstants.PacketType.PACKET_COUNT)
		{
			throw new Exception($"ERROR: Unknown packet type encountered: ({typeByte})");
		}
		NetworkingConstants.PacketType type = (NetworkingConstants.PacketType)typeByte;
		List<byte> payload = new List<byte>();
		Functions.Log($"Received packet of type {type}", includeFullPath: true);
		switch(type)
		{

			case NetworkingConstants.PacketType.ServerCreateRequest:
			{
				string name = Functions.DeserializeJson<ServerPackets.CreateRequest>(bytes!).name!;
				Predicate<Room> nameExists = x => x.players[0].Name == name || x.players[1].Name == name;
				if(name.Contains("µ"))
				{
					payload = Functions.GeneratePayload<ServerPackets.CreateResponse>(new ServerPackets.CreateResponse
					{
						success = false,
						reason = "Your name cannot contain 'µ'"
					});
				}
				else if(waitingList.Exists(nameExists) || runningList.Exists(nameExists))
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
					for(int i = config.room_min_port; i <= config.room_max_port; i++)
					{
						bool free = true;
						foreach(Room r in waitingList)
						{
							if(r.port == i)
							{
								free = false;
								break;
							}
						}
						if(free)
						{
							foreach(Room r in runningList)
							{
								if(r.port == i)
								{
									free = false;
									break;
								}
							}
						}
						if(free)
						{
							if(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Any(x => x.LocalEndPoint.Port == i))
							{
								free = false;
							}
						}
						if(free)
						{
							Functions.Log($"Next port: {i}");
							currentPort = i;
							break;
						}
					}
					if(currentPort == -1)
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
			break;
			case NetworkingConstants.PacketType.ServerJoinRequest:
			{
				ServerPackets.JoinRequest request = Functions.DeserializeJson<ServerPackets.JoinRequest>(bytes!);
				Predicate<Room> nameExists = x => x.players[0].Name == request.name || x.players[1].Name == request.name;
				if(request.name!.Contains("µ"))
				{
					payload = Functions.GeneratePayload<ServerPackets.JoinResponse>(new ServerPackets.JoinResponse
					{
						success = false,
						reason = "Your name cannot contain 'µ'"
					});
				}
				else if(waitingList.FindIndex(nameExists) != -1 || runningList.FindIndex(nameExists) != -1)
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
					if(index == -1)
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
			break;
			case NetworkingConstants.PacketType.ServerLeaveRequest:
			{
				string name = Functions.DeserializeJson<ServerPackets.LeaveRequest>(bytes!).name!;
				Predicate<Room> nameExists = x => x.players[0].Name == name || x.players[1].Name == name;
				int index = waitingList.FindIndex(nameExists);
				if(index == -1)
				{
					index = runningList.FindIndex(nameExists);
					if(index == -1)
					{
						payload = Functions.GeneratePayload<ServerPackets.LeaveResponse>(new ServerPackets.LeaveResponse
						{
							success = false,
							reason = "No player with that name found in a room"
						});
					}
					else
					{
						if(runningList[index].players[0].Name == name)
						{
							runningList[index].players[0].Name = null;
							runningList[index].players[0].Decklist = null;
							runningList[index].players[0].ready = false;
						}
						else
						{
							runningList[index].players[1].Name = null;
							runningList[index].players[1].Decklist = null;
							runningList[index].players[1].ready = false;
						}
						if(runningList[index].players[0].Name == null && runningList[index].players[1].Name == null)
						{
							runningList.RemoveAt(index);
						}
						else
						{
							waitingList.Add(runningList[index]);
							runningList.RemoveAt(index);
						}
						payload = Functions.GeneratePayload<ServerPackets.LeaveResponse>(new ServerPackets.LeaveResponse
						{
							success = true
						});
					}
				}
				else
				{
					if(waitingList[index].players[0].Name == name)
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
					if(waitingList[index].players[0].Name == null && waitingList[index].players[1].Name == null)
					{
						waitingList.RemoveAt(index);
					}
					payload = Functions.GeneratePayload<ServerPackets.LeaveResponse>(new ServerPackets.LeaveResponse
					{
						success = true
					});
				}
			}
			break;
			case NetworkingConstants.PacketType.ServerRoomsRequest:
			{
				if(waitingList.Exists(x => x.players[0].Name == null))
				{
					Functions.Log($"There is a player whose name is null", severity: Functions.LogSeverity.Error, includeFullPath: true);
					return HandlePacketReturn.Continue;
				}
				payload = Functions.GeneratePayload<ServerPackets.RoomsResponse>(new ServerPackets.RoomsResponse
				{
					rooms = waitingList.ConvertAll(x => x.players[0].Name!).ToArray()
				});
			}
			break;
			case NetworkingConstants.PacketType.ServerStartRequest:
			{
				Functions.Log("----START REQUEST HANDLING----", includeFullPath: true);
				ServerPackets.StartRequest request = Functions.DeserializeJson<ServerPackets.StartRequest>(bytes!);
				if(request.decklist.Length != GameConstants.DECK_SIZE + 3)
				{
					payload = Functions.GeneratePayload<ServerPackets.StartResponse>(new ServerPackets.StartResponse
					{
						success = ServerPackets.StartResponse.Result.Failure,
						reason = "Your deck has the wrong size",
					});

				}
				Predicate<Room> nameExists = x => x.players[0].Name == request.name || x.players[1].Name == request.name;
				int index = waitingList.FindIndex(nameExists);
				Functions.Log("Waiting List index: " + index, includeFullPath: true);
				if(index == -1)
				{
					// We still have to check if the other player started, moving the room to the running list
					index = runningList.FindIndex(nameExists);
					Functions.Log("Running List Index: " + index, includeFullPath: true);
					if(index == -1)
					{
						payload = Functions.GeneratePayload<ServerPackets.StartResponse>(new ServerPackets.StartResponse
						{
							success = ServerPackets.StartResponse.Result.Failure,
							reason = "You are not part of any waiting room"
						});
					}
					else
					{
						Room room = runningList[index];
						int player = (room.players[0].Name == request.name) ? 0 : 1;
						Functions.Log("Player: " + player, includeFullPath: true);
						room.players[player].ready = true;
						room.players[player].noshuffle = request.noshuffle;
						room.players[player].Decklist = request.decklist;
						room.players[player].stream = new NetworkStream(stream.Socket);
						if(room.StartGame())
						{
							payload = Functions.GeneratePayload<ServerPackets.StartResponse>(new ServerPackets.StartResponse
							{
								success = ServerPackets.StartResponse.Result.SuccessButWaiting,
							});
						}
						else
						{
							Functions.Log("Could not create the core", severity: Functions.LogSeverity.Error, includeFullPath: true);
							List<byte> startPayload = Functions.GeneratePayload<ServerPackets.StartResponse>(new ServerPackets.StartResponse
							{
								success = ServerPackets.StartResponse.Result.Failure,
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
					if(waitingList[index].players[1 - player].Name == null)
					{
						Functions.Log("No opponent", includeFullPath: true);
						payload = Functions.GeneratePayload<ServerPackets.StartResponse>(new ServerPackets.StartResponse
						{
							success = ServerPackets.StartResponse.Result.Failure,
							reason = "You have no opponent",
						});
					}
					else
					{
						Functions.Log("Opponent present", includeFullPath: true);
						if(waitingList[index].players.All(x => x.ready))
						{
							Functions.Log("All players ready", includeFullPath: true);
							Room room = waitingList[index];
							runningList.Add(room);
							waitingList.RemoveAt(index);
							room.players[player].stream = new NetworkStream(stream.Socket);
							payload = Functions.GeneratePayload<ServerPackets.StartResponse>(new ServerPackets.StartResponse
							{
								success = ServerPackets.StartResponse.Result.SuccessButWaiting,
							});
							stream.Write(payload.ToArray(), 0, payload.Count);
							return HandlePacketReturn.ContinueKeepStream;
						}
						else
						{
							Functions.Log("Opponent not ready", includeFullPath: true);
							payload = Functions.GeneratePayload<ServerPackets.StartResponse>(new ServerPackets.StartResponse
							{
								success = ServerPackets.StartResponse.Result.Failure,
								reason = "Your opponent isn't ready yet"
							});
						}
					}
				}
				Functions.Log("----END----", includeFullPath: true);
			}
			break;
			case NetworkingConstants.PacketType.ServerAdditionalCardsRequest:
			{
				string fullAdditionalCardsPath = Path.Combine(baseDir, config.additional_cards_path);
				if(!File.Exists(fullAdditionalCardsPath) ||
					File.GetLastWriteTime(config.additional_cards_path) > lastAdditionalCardsTimestamp)
				{
					ProcessStartInfo info = new ProcessStartInfo
					{
						Arguments = config.core_info.Arguments + " --additional_cards_path=" + fullAdditionalCardsPath,
						CreateNoWindow = config.core_info.CreateNoWindow,
						UseShellExecute = config.core_info.UseShellExecute,
						FileName = config.core_info.FileName,
						WorkingDirectory = config.core_info.WorkingDirectory,
					};
					Process.Start(info)?.WaitForExit(10000);
				}

				if(File.Exists(fullAdditionalCardsPath))
				{
					payload = Functions.GeneratePayload<ServerPackets.AdditionalCardsResponse>(JsonSerializer.Deserialize<ServerPackets.AdditionalCardsResponse>(File.ReadAllText(fullAdditionalCardsPath), NetworkingConstants.jsonIncludeOption)!);
					Functions.Log($"additional cards packet length: {payload.Count}");
				}
				else
				{
					Functions.Log("No additional cards file exists", severity: Functions.LogSeverity.Warning);
					payload = Functions.GeneratePayload<ServerPackets.AdditionalCardsResponse>(new ServerPackets.AdditionalCardsResponse());
				}
			}
			break;
			default:
			{
				throw new Exception($"ERROR: Unable to process this packet: Packet type: {type} | {Encoding.UTF8.GetString(bytes!)}");
			}
		}
		stream.Write(payload.ToArray(), 0, payload.Count);
		return HandlePacketReturn.Continue;
	}
}
