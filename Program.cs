using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using CardGameUtils;
using CardGameUtils.Structs;
using static CardGameUtils.Structs.NetworkingStructs;

namespace CardGameServer;

class Program
{
	public static string baseDir = AppDomain.CurrentDomain.BaseDirectory;
	public static ServerConfig config = new("additional_cards/", 7043, 37042, 39942, new CoreInfo());
	public static DateTime lastAdditionalCardsTimestamp;
	public static string? seed;
	public static SHA384 sha = SHA384.Create();
	// TODO: MAKE THIS THREAD-SAFE SOMEHOW
	public static List<Room> waitingList = [];
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
		PlatformServerConfig platformConfig = JsonSerializer.Deserialize<PlatformServerConfig>(File.ReadAllText(configLocation), GenericConstants.platformServerConfigSerialization);
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
			lastAdditionalCardsTimestamp = JsonSerializer.Deserialize<ServerPackets.AdditionalCardsResponse>(File.ReadAllText(config.additional_cards_path), GenericConstants.packetSerialization)!.time;
		}
		TcpListener listener = new(IPAddress.Parse("127.0.0.1"), config.port);
		byte[] nowBytes = Encoding.UTF8.GetBytes(DateTime.Now.ToString());
		seed = Convert.ToBase64String(sha.ComputeHash(nowBytes));
		listener.Start();
		while(true)
		{
			if(listener.Pending())
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
			HandleRooms();
		}
		listener.Stop();
	}

	private static void HandleRooms()
	{
		if(waitingList.Count == 0)
		{
			Thread.Sleep(100);
		}
		for(int roomIndex = waitingList.Count - 1; roomIndex >= 0; roomIndex--)
		{
			Room room = waitingList[roomIndex];
			for(int playerIndex = 0; playerIndex < waitingList[roomIndex].players.Length; playerIndex++)
			{
				Room.Player? player = room.players[playerIndex];
				if(player != null && player.stream != null &&
					player.stream.CanRead &&
					player.stream.DataAvailable)
				{
					(byte type, byte[]? bytes)? packet = Functions.TryReceiveRawPacket(player.stream, 100);
					if(packet != null)
					{
						if(packet.Value.type >= (byte)NetworkingConstants.PacketType.PACKET_COUNT)
						{
							Functions.Log($"Unrecognized packet type ({packet.Value.type})");
							throw new Exception($"Unrecognized packet type ({packet.Value.type})");
						}
						NetworkingConstants.PacketType type = (NetworkingConstants.PacketType)packet.Value.type;
						byte[]? bytes = packet.Value.bytes;
						switch(type)
						{
							case NetworkingConstants.PacketType.ServerLeaveRequest:
							{
								player.stream.Dispose();
								room.players[playerIndex] = null;
								if(room.players[0] == null && room.players[1] == null)
								{
									waitingList.RemoveAt(roomIndex);
									return;
								}
								else
								{
									if(room.players[1 - playerIndex]?.stream.Socket.Connected ?? false)
									{
										room.players[1 - playerIndex]?.stream.Write(Functions.GeneratePayload(new ServerPackets.OpponentChangedResponse(null)));
									}
								}
							}
							break;
							case NetworkingConstants.PacketType.ServerStartRequest:
							{
								Functions.Log("----START REQUEST HANDLING----", includeFullPath: true);
								ServerPackets.StartRequest request = Functions.DeserializeJson<ServerPackets.StartRequest>(bytes!);
								if(request.decklist.Length != GameConstants.DECK_SIZE + 3)
								{
									player.stream.Write(Functions.GeneratePayload(new ServerPackets.StartResponse
									{
										success = ServerPackets.StartResponse.Result.Failure,
										reason = "Your deck has the wrong size",
									}));
									break;
								}
								Functions.Log("Player: " + playerIndex, includeFullPath: true);
								player.ready = true;
								player.noshuffle = request.noshuffle;
								player.Decklist = request.decklist;
								if(room.players[1 - playerIndex] == null)
								{
									Functions.Log("No opponent", includeFullPath: true);
									player.stream.Write(Functions.GeneratePayload(new ServerPackets.StartResponse
									{
										success = ServerPackets.StartResponse.Result.Failure,
										reason = "You have no opponent",
									}));
									break;
								}
								Functions.Log("Opponent present", includeFullPath: true);
								if(Array.TrueForAll(waitingList[roomIndex].players, x => x?.ready ?? false))
								{
									Functions.Log("All players ready", includeFullPath: true);
									if(room.StartGame())
									{
										foreach(Room.Player? p in room.players)
										{
											if(p != null && p.stream.Socket.Connected)
											{
												p.stream.Write(Functions.GeneratePayload(new ServerPackets.StartResponse
												{
													success = ServerPackets.StartResponse.Result.SuccessButWaiting,
												}));
											}
										}
									}
									else
									{
										Functions.Log("Could not create the core", severity: Functions.LogSeverity.Error, includeFullPath: true);
										byte[] startPayload = Functions.GeneratePayload(new ServerPackets.StartResponse
										{
											success = ServerPackets.StartResponse.Result.Failure,
											reason = "Could not create a core"
										});
									}
								}
								else
								{
									Functions.Log("Opponent not ready", includeFullPath: true);
									player.stream.Write(Functions.GeneratePayload(new ServerPackets.StartResponse
									{
										success = ServerPackets.StartResponse.Result.Failure,
										reason = "Your opponent isn't ready yet"
									}));
								}
								Functions.Log("----END----", includeFullPath: true);
							}
							break;
						}
					}
				}
			}
		}
	}

	private enum HandlePacketReturn
	{
		Break,
		Continue,
		ContinueKeepStream,
	}

	private static void CleanupRooms()
	{
		int waitingCount = 0;
		for(int i = waitingList.Count - 1; i >= 0; i--)
		{
			if((DateTime.Now - waitingList[i].startTime).Days > 1 || waitingList[i].isFinished)
			{
				waitingList[i].players[0]?.stream.Close();
				waitingList[i].players[1]?.stream.Close();
				waitingList.RemoveAt(i);
				waitingCount++;
			}
		}
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
		byte[]? payload = null;
		Functions.Log($"Received packet of type {type}", includeFullPath: true);
		switch(type)
		{
			case NetworkingConstants.PacketType.ServerCreateRequest:
			{
				string name = Functions.DeserializeJson<ServerPackets.CreateRequest>(bytes!).name!;
				if(waitingList.Exists(x => x.players[0]?.Name == name || x.players[1]?.Name == name))
				{
					payload = Functions.GeneratePayload(new ServerPackets.CreateResponse
					(
						success: false,
						reason: "Oh oh, sorry kiddo, looks like someone else already has that name. Why don't you pick something else? (Please watch SAO Abridged if you don't get this reference)"
					));
				}
				else
				{
					string id = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(seed + name))).Replace("-", "");
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
							if(Array.Exists(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections(), x => x.LocalEndPoint.Port == i))
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
						payload = Functions.GeneratePayload(new ServerPackets.CreateResponse
						(
							success: false,
							reason: "No free port found"
						));
					}
					else
					{
						waitingList.Add(new Room(name, id, currentPort, stream));
						payload = Functions.GeneratePayload(new ServerPackets.CreateResponse
						(
							success: true
						));
						stream.Write(payload);
						return HandlePacketReturn.ContinueKeepStream;
					}
				}
			}
			break;
			case NetworkingConstants.PacketType.ServerJoinRequest:
			{
				ServerPackets.JoinRequest request = Functions.DeserializeJson<ServerPackets.JoinRequest>(bytes!);
				if(waitingList.FindIndex(x => x.players[0]?.Name == request.name || x.players[1]?.Name == request.name) != -1)
				{
					payload = Functions.GeneratePayload(new ServerPackets.JoinResponse
					(
						success: false,
						reason: "Oh oh, sorry kiddo, looks like someone else already has that name. Why don't you pick something else? (Please watch SAO Abridged if you don't get this reference)"
					));
				}
				else
				{
					int index = waitingList.FindIndex(x => x.players[0]?.Name == request.targetName || x.players[1]?.Name == request.targetName);
					if(index == -1)
					{
						payload = Functions.GeneratePayload(new ServerPackets.JoinResponse
						(
							success: false,
							reason: "No player with that name hosts a game right now"
						));
					}
					else
					{
						string id = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(seed + request.name))).Replace("-", "");
						int playerIndex = waitingList[index].players[0] == null ? 0 : 1;
						waitingList[index].players[playerIndex] = new Room.Player(Name: request.name, id: id, stream: stream, ready: false, noshuffle: false);
						payload = Functions.GeneratePayload(new ServerPackets.JoinResponse
						(
							success: true
						));
						if(waitingList[index].players[1 - playerIndex]!.stream != null && waitingList[index].players[1 - playerIndex]!.stream.Socket.Connected)
						{
							waitingList[index].players[1 - playerIndex]!.stream.Write(Functions.GeneratePayload(new ServerPackets.OpponentChangedResponse(request.name)));
						}
						stream.Write(payload);
						return HandlePacketReturn.ContinueKeepStream;
					}
				}
			}
			break;
			case NetworkingConstants.PacketType.ServerRoomsRequest:
			{
				if(waitingList.Exists(x => x.players[0]?.Name == null && x.players[1]?.Name == null))
				{
					Functions.Log($"There is a player whose name is null", severity: Functions.LogSeverity.Error, includeFullPath: true);
					return HandlePacketReturn.Continue;
				}
				payload = Functions.GeneratePayload(new ServerPackets.RoomsResponse([.. waitingList.FindAll(x => !Array.TrueForAll(x.players, y => y?.ready ?? false)).ConvertAll(x => x.players[0]?.Name ?? x.players[1]?.Name)]));
			}
			break;
			case NetworkingConstants.PacketType.ServerAdditionalCardsRequest:
			{
				string fullAdditionalCardsPath = Path.Combine(baseDir, config.additional_cards_path);
				if(!File.Exists(fullAdditionalCardsPath) ||
					JsonSerializer.Deserialize<ServerPackets.AdditionalCardsResponse>(File.ReadAllText(fullAdditionalCardsPath), GenericConstants.packetSerialization)?.time > lastAdditionalCardsTimestamp)
				{
					ProcessStartInfo info = new()
					{
						Arguments = config.core_info.Arguments + " --additional_cards_path=" + fullAdditionalCardsPath,
						CreateNoWindow = config.core_info.CreateNoWindow,
						UseShellExecute = config.core_info.UseShellExecute,
						FileName = config.core_info.FileName,
						WorkingDirectory = config.core_info.WorkingDirectory,
					};
					_ = (Process.Start(info)?.WaitForExit(10000));
				}
				if(File.Exists(fullAdditionalCardsPath))
				{
					ServerPackets.AdditionalCardsResponse response = JsonSerializer.Deserialize<ServerPackets.AdditionalCardsResponse>(File.ReadAllText(fullAdditionalCardsPath), GenericConstants.packetSerialization)!;
					lastAdditionalCardsTimestamp = response.time;
					payload = Functions.GeneratePayload(response);
					Functions.Log($"additional cards packet length: {payload.Length}");
				}
				else
				{
					Functions.Log("No additional cards file exists", severity: Functions.LogSeverity.Warning);
					payload = Functions.GeneratePayload(new ServerPackets.AdditionalCardsResponse(DateTime.Now, []));
				}
			}
			break;
			default:
			{
				throw new Exception($"ERROR: Unable to process this packet: Packet type: {type} | {Encoding.UTF8.GetString(bytes!)}");
			}
		}
		stream.Write(payload);
		return HandlePacketReturn.Continue;
	}
}
