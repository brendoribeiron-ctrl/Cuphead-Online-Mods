using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// MOD ONLINE: salas na rede local. O host manda um aviso UDP por segundo e quem esta
// no lobby escuta e lista as salas, sem precisar de codigo nem IP.
public static class LanDiscovery
{
	public class Room
	{
		public string Address;

		public int Port;

		public string HostName;

		public int Players;

		public int MaxPlayers;

		public bool InGame;

		public float LastSeen;
	}

	public const int BeaconPort = 7778;

	private const string Magic = "CUPHEADLOBBY1";

	private static readonly string InstanceId = Guid.NewGuid().ToString("N").Substring(0, 8);

	private static readonly object sync = new object();

	private static readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();

	private static UdpClient listener;

	private static Thread listenThread;

	private static UdpClient sender;

	private static List<IPEndPoint> targets;

	private static volatile float now;

	public static bool Listening => listener != null;

	public static void StartListening()
	{
		if (listener != null)
		{
			return;
		}
		try
		{
			UdpClient udp = new UdpClient();
			udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, optionValue: true);
			udp.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
			listener = udp;
		}
		catch (Exception ex)
		{
			NetLog.Info("nao consegui escutar salas da rede local: " + ex.Message);
			return;
		}
		UdpClient mine = listener;
		listenThread = new Thread((ThreadStart)delegate
		{
			ListenLoop(mine);
		});
		listenThread.IsBackground = true;
		listenThread.Start();
	}

	public static void StopListening()
	{
		UdpClient udp = listener;
		listener = null;
		if (udp != null)
		{
			udp.Close();
		}
		lock (sync)
		{
			rooms.Clear();
		}
	}

	// Salas vistas nos ultimos 3 segundos
	public static List<Room> GetRooms()
	{
		now = Time.unscaledTime;
		List<Room> list = new List<Room>();
		lock (sync)
		{
			foreach (Room room in rooms.Values)
			{
				if (now - room.LastSeen < 3f)
				{
					list.Add(room);
				}
			}
		}
		list.Sort((Room a, Room b) => string.Compare(a.HostName, b.HostName, StringComparison.OrdinalIgnoreCase));
		return list;
	}

	private static void ListenLoop(UdpClient udp)
	{
		while (listener == udp)
		{
			byte[] data;
			IPEndPoint from = null;
			try
			{
				data = udp.Receive(ref from);
			}
			catch (Exception)
			{
				if (listener != udp)
				{
					return;
				}
				continue;
			}
			string[] parts = Encoding.UTF8.GetString(data).Split('|');
			if (parts.Length < 7 || parts[0] != Magic || parts[1] == InstanceId)
			{
				continue;
			}
			if (!int.TryParse(parts[3], out var port) || !int.TryParse(parts[4], out var players) || !int.TryParse(parts[5], out var max))
			{
				continue;
			}
			// O mesmo host chega por varias placas (rede, VPN, loopback): uma linha por sala
			string key = parts[1] + ":" + port;
			lock (sync)
			{
				if (!rooms.TryGetValue(key, out var room))
				{
					room = new Room();
					rooms[key] = room;
				}
				if (room.Address == null || Rank(from.Address) < Rank(IPAddress.Parse(room.Address)))
				{
					room.Address = from.Address.ToString();
				}
				room.Port = port;
				room.HostName = parts[2];
				room.Players = players;
				room.MaxPlayers = max;
				room.InGame = parts[6] == "1";
				room.LastSeen = now;
			}
		}
	}

	// Menor = melhor caminho: rede local, depois VPN/outros, por ultimo o proprio PC
	private static int Rank(IPAddress address)
	{
		if (IPAddress.IsLoopback(address))
		{
			return 2;
		}
		byte[] b = address.GetAddressBytes();
		bool lan = b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] < 32);
		return lan ? 0 : 1;
	}

	// Chamado pelo host uma vez por segundo
	public static void SendBeacon(string hostName, int port, int players, int maxPlayers, bool inGame)
	{
		now = Time.unscaledTime;
		try
		{
			if (sender == null)
			{
				sender = new UdpClient();
				sender.EnableBroadcast = true;
				targets = BuildTargets();
			}
			string safeName = hostName.Replace("|", "/");
			byte[] data = Encoding.UTF8.GetBytes(Magic + "|" + InstanceId + "|" + safeName + "|" + port + "|" + players + "|" + maxPlayers + "|" + (inGame ? "1" : "0"));
			foreach (IPEndPoint target in targets)
			{
				try
				{
					sender.Send(data, data.Length, target);
				}
				catch (Exception)
				{
				}
			}
		}
		catch (Exception ex)
		{
			NetLog.Info("aviso de sala falhou: " + ex.Message);
		}
	}

	public static void StopBeacon()
	{
		if (sender != null)
		{
			sender.Close();
			sender = null;
		}
	}

	// Broadcast geral, broadcast /24 de cada placa (inclui VPNs tipo Radmin/ZeroTier)
	// e o proprio PC, para testar duas copias do jogo na mesma maquina
	private static List<IPEndPoint> BuildTargets()
	{
		List<IPEndPoint> list = new List<IPEndPoint>();
		list.Add(new IPEndPoint(IPAddress.Broadcast, BeaconPort));
		list.Add(new IPEndPoint(IPAddress.Loopback, BeaconPort));
		foreach (string ip in LocalAddresses())
		{
			byte[] b = IPAddress.Parse(ip).GetAddressBytes();
			b[3] = byte.MaxValue;
			list.Add(new IPEndPoint(new IPAddress(b), BeaconPort));
		}
		return list;
	}

	public static List<string> LocalAddresses()
	{
		List<string> list = new List<string>();
		try
		{
			foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName()))
			{
				if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
				{
					list.Add(address.ToString());
				}
			}
		}
		catch (Exception)
		{
		}
		return list;
	}
}
