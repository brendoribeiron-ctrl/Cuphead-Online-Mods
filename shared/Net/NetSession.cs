using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// MOD ONLINE: camada de sessao do multiplayer. Cuida da conexao TCP, da entrada na sala
// (versao, nome, token), da lista de jogadores, do pronto/iniciar, do ping e da reconexao.
//
// Mensagem na rede: [tipo:byte][tamanho:int32][payload]. Tipos 0x00-0x1F sao desta
// classe; 0x20-0x3F sao do netcode (NetProtocol); 0x40-0x4F sao do modo tela (OnlineStream).
// Slot 0 e sempre o host, slot 1 o amigo.
public class NetSession : MonoBehaviour
{
	public enum Role
	{
		None,
		Host,
		Guest
	}

	public enum SessionState
	{
		Offline,
		Connecting,
		Lobby,
		InGame,
		Reconnecting
	}

	// Como a partida roda depois de iniciar
	public enum GameMode : byte
	{
		// cada PC roda o proprio jogo e o netcode sincroniza (NetcodeSync)
		Sync = 0,
		// o amigo ve a tela do host e manda os controles (OnlineStream)
		Stream = 1
	}

	public delegate void MessageHandler(int fromSlot, byte[] payload);

	public class PlayerInfo
	{
		public int Slot;

		public string Name;

		public bool Ready;

		public bool Connected;

		public int PingMs;

		// segundos que faltam para a vaga ser liberada (so quando desconectado)
		public float ReserveLeft;
	}

	public const int ProtocolVersion = 1;

	public const int DefaultPort = 7777;

	public const int MaxPlayers = 2;

	public const float LobbyReserveSeconds = 20f;

	public const float GameReserveSeconds = 60f;

	public const float ReconnectSeconds = 60f;

	private const int TimeoutMs = 6000;

	private const float HelloTimeout = 5f;

	private const int MaxMessageBytes = 8388608;

	public const byte MsgHello = 0x01;

	public const byte MsgWelcome = 0x02;

	public const byte MsgLobbyState = 0x03;

	public const byte MsgSetReady = 0x04;

	public const byte MsgStartGame = 0x05;

	public const byte MsgPing = 0x06;

	public const byte MsgPong = 0x07;

	public const byte MsgBye = 0x08;

	public const byte LastSessionType = 0x1F;

	private static readonly byte[] Empty = new byte[0];

	private class Connection
	{
		public TcpClient Client;

		public NetworkStream Stream;

		public readonly Queue<byte[]> Queue = new Queue<byte[]>();

		public readonly Dictionary<byte, byte[]> Latest = new Dictionary<byte, byte[]>();

		public readonly AutoResetEvent Signal = new AutoResetEvent(initialState: false);

		public volatile bool Welcomed;

		public volatile int Slot = -1;

		public volatile int LastReceive;

		public volatile bool CloseAfterSend;

		public int Closed;

		public int Generation;

		public float OpenedAt;

		public string Remote = "?";

		public void Enqueue(byte type, byte[] payload)
		{
			byte[] framed = Frame(type, payload);
			lock (Queue)
			{
				Queue.Enqueue(framed);
			}
			Signal.Set();
		}

		public void SetLatest(byte type, byte[] payload)
		{
			byte[] framed = Frame(type, payload);
			lock (Queue)
			{
				Latest[type] = framed;
			}
			Signal.Set();
		}
	}

	private class Slot
	{
		public int Index;

		public string Name;

		public string Token;

		public bool Ready;

		public Connection Conn;

		public float LostAt;

		public int PingMs;
	}

	private enum EventKind
	{
		Accepted,
		Connected,
		ConnectFailed,
		Message,
		Closed
	}

	private struct NetEvent
	{
		public EventKind Kind;

		public Connection Conn;

		public byte Type;

		public byte[] Payload;

		public string Error;

		public int Generation;
	}

	public static NetSession Instance { get; private set; }

	public Role CurrentRole { get; private set; }

	public SessionState State { get; private set; }

	public GameMode Mode { get; private set; }

	public string Status { get; private set; } = "Desconectado";

	public int LocalSlot { get; private set; }

	public int Port { get; private set; } = DefaultPort;

	// Endereco usado para entrar (lado do amigo)
	public string JoinAddress { get; private set; }

	public int ReconnectAttempt { get; private set; }

	public string LocalName = "Jogador";

	// Modo com que uma sala nova comeca (o host pode trocar no lobby; -mode stream|sync)
	public static GameMode DefaultMode = GameMode.Sync;

	public bool IsHost => CurrentRole == Role.Host;

	public bool IsGuest => CurrentRole == Role.Guest;

	public bool InSession => State != SessionState.Offline;

	public float ReconnectTimeLeft => (State == SessionState.Reconnecting) ? Mathf.Max(0f, reconnectDeadline - Time.unscaledTime) : 0f;

	// Eventos na thread principal
	public event Action GameStarted;

	public event Action<int> PlayerJoined;

	public event Action<int> PlayerLeft;

	// A conexao caiu, mas a vaga continua reservada (slot do outro lado: 1 no host, 0 no amigo)
	public event Action<int> PlayerDisconnected;

	public event Action<int> PlayerReconnected;

	// A sessao acabou de vez (saiu, foi removido, host fechou ou reconexao desistiu)
	public event Action<string> SessionEnded;

	private readonly MessageHandler[] mainHandlers = new MessageHandler[256];

	private readonly MessageHandler[] threadHandlers = new MessageHandler[256];

	private readonly object inboxLock = new object();

	private readonly Queue<NetEvent> inbox = new Queue<NetEvent>();

	private int generation;

	// ---------- host ----------

	private TcpListener listener;

	private Thread acceptThread;

	private volatile bool listening;

	private readonly Slot[] slots = new Slot[MaxPlayers];

	private readonly List<Connection> pending = new List<Connection>();

	private bool lobbyDirty;

	private float nextBeacon;

	// ---------- amigo ----------

	private Connection hostConn;

	private string token = string.Empty;

	private int joinPort;

	private bool connecting;

	private float reconnectDeadline;

	private float nextAttempt;

	private bool gameStartedOnce;

	private int hostPing;

	private readonly List<PlayerInfo> remotePlayers = new List<PlayerInfo>();

	private float nextHeartbeat;

	private void Awake()
	{
		Instance = this;
	}

	private void OnApplicationQuit()
	{
		Leave();
	}

	private void OnDestroy()
	{
		Leave();
	}

	// ---------- API ----------

	// Handler na thread principal (padrao) ou direto na thread de leitura da rede
	// (para netcode que precisa do pacote no meio do frame). Tipos 0x00-0x1F sao reservados.
	public void RegisterHandler(byte type, MessageHandler handler, bool onNetworkThread = false)
	{
		if (type <= LastSessionType)
		{
			throw new ArgumentException("tipo reservado do lobby: " + type);
		}
		if (onNetworkThread)
		{
			threadHandlers[type] = handler;
			mainHandlers[type] = null;
		}
		else
		{
			mainHandlers[type] = handler;
			threadHandlers[type] = null;
		}
	}

	// Manda para todos os outros (toSlot = -1) ou para um slot. Pode ser chamado de qualquer thread.
	public void Send(byte type, byte[] payload, int toSlot = -1)
	{
		foreach (Connection c in Targets(toSlot))
		{
			c.Enqueue(type, payload ?? Empty);
		}
	}

	// Igual ao Send, mas se ainda tem um pacote desse tipo esperando para sair, troca pelo novo
	// (bom para imagem da tela e estado do controle: so o mais recente importa)
	public void SendLatest(byte type, byte[] payload, int toSlot = -1)
	{
		foreach (Connection c in Targets(toSlot))
		{
			c.SetLatest(type, payload ?? Empty);
		}
	}

	public bool IsConnected(int slot)
	{
		if (CurrentRole == Role.Host)
		{
			if (slot == 0)
			{
				return true;
			}
			Slot s = (slot >= 0 && slot < MaxPlayers) ? slots[slot] : null;
			return s != null && s.Conn != null && s.Conn.Welcomed;
		}
		if (CurrentRole == Role.Guest)
		{
			if (slot == LocalSlot)
			{
				return true;
			}
			return hostConn != null && hostConn.Welcomed && hostConn.Closed == 0;
		}
		return false;
	}

	// Algum outro jogador conectado agora
	public bool PeerConnected => IsConnected((LocalSlot == 0) ? 1 : 0);

	public int PingMs
	{
		get
		{
			if (CurrentRole == Role.Guest)
			{
				return hostPing;
			}
			Slot s = slots[1];
			return (s != null) ? s.PingMs : 0;
		}
	}

	public List<PlayerInfo> GetPlayers()
	{
		List<PlayerInfo> list = new List<PlayerInfo>();
		if (CurrentRole == Role.Host)
		{
			float reserve = (State == SessionState.InGame) ? GameReserveSeconds : LobbyReserveSeconds;
			foreach (Slot s in slots)
			{
				if (s != null)
				{
					list.Add(new PlayerInfo
					{
						Slot = s.Index,
						Name = s.Name,
						Ready = s.Ready,
						Connected = (s.Index == 0 || (s.Conn != null && s.Conn.Welcomed)),
						PingMs = s.PingMs,
						ReserveLeft = (s.Index != 0 && s.Conn == null) ? Mathf.Max(0f, reserve - (Time.unscaledTime - s.LostAt)) : 0f
					});
				}
			}
		}
		else if (CurrentRole == Role.Guest)
		{
			list.AddRange(remotePlayers);
		}
		return list;
	}

	public bool CanStart
	{
		get
		{
			if (CurrentRole != Role.Host || State != SessionState.Lobby)
			{
				return false;
			}
			Slot s = slots[1];
			return s != null && s.Ready && s.Conn != null && s.Conn.Welcomed;
		}
	}

	public bool LocalReady
	{
		get
		{
			if (CurrentRole == Role.Host)
			{
				return true;
			}
			foreach (PlayerInfo p in remotePlayers)
			{
				if (p.Slot == LocalSlot)
				{
					return p.Ready;
				}
			}
			return false;
		}
	}

	// ---------- host ----------

	public bool Host(int port)
	{
		Leave();
		try
		{
			listener = new TcpListener(IPAddress.Any, port);
			listener.Start();
		}
		catch (Exception ex)
		{
			listener = null;
			Status = "Nao consegui abrir a porta " + port + ": " + ex.Message;
			return false;
		}
		generation++;
		CurrentRole = Role.Host;
		State = SessionState.Lobby;
		LocalSlot = 0;
		Port = port;
		Mode = DefaultMode;
		gameStartedOnce = false;
		slots[0] = new Slot
		{
			Index = 0,
			Name = LocalName,
			Ready = true
		};
		for (int i = 1; i < MaxPlayers; i++)
		{
			slots[i] = null;
		}
		listening = true;
		TcpListener mine = listener;
		int gen = generation;
		acceptThread = new Thread((ThreadStart)delegate
		{
			AcceptLoop(mine, gen);
		});
		acceptThread.IsBackground = true;
		acceptThread.Start();
		NatHelper.Begin(port);
		Status = "Sala criada. Passe o codigo para o seu amigo.";
		lobbyDirty = true;
		NetLog.Info("sala criada na porta " + port);
		return true;
	}

	public void SetMode(GameMode mode)
	{
		if (CurrentRole == Role.Host && State == SessionState.Lobby)
		{
			Mode = mode;
			lobbyDirty = true;
		}
	}

	public void StartGame()
	{
		if (!CanStart)
		{
			return;
		}
		State = SessionState.InGame;
		Status = "Partida iniciada";
		foreach (Connection c in Targets(-1))
		{
			c.Enqueue(MsgStartGame, new byte[1] { (byte)Mode });
		}
		lobbyDirty = true;
		FireGameStarted();
	}

	public void Kick(int slot)
	{
		if (CurrentRole != Role.Host || slot <= 0 || slot >= MaxPlayers || slots[slot] == null)
		{
			return;
		}
		Slot s = slots[slot];
		slots[slot] = null;
		if (s.Conn != null)
		{
			SendBye(s.Conn, "O host tirou voce da sala");
		}
		lobbyDirty = true;
		Status = s.Name + " foi removido da sala";
		Fire(PlayerLeft, slot);
	}

	private void AcceptLoop(TcpListener l, int gen)
	{
		while (listening && listener == l)
		{
			TcpClient tcp;
			try
			{
				tcp = l.AcceptTcpClient();
			}
			catch (Exception)
			{
				return;
			}
			tcp.NoDelay = true;
			Connection c = new Connection
			{
				Client = tcp,
				Stream = tcp.GetStream(),
				Generation = gen,
				LastReceive = Environment.TickCount
			};
			try
			{
				c.Remote = tcp.Client.RemoteEndPoint.ToString();
			}
			catch (Exception)
			{
			}
			Post(new NetEvent
			{
				Kind = EventKind.Accepted,
				Conn = c,
				Generation = gen
			});
		}
	}

	private void HostHandle(Connection c, byte type, byte[] payload)
	{
		BinaryReader r = Reader(payload);
		switch (type)
		{
		case MsgHello:
			HostHello(c, r);
			break;
		case MsgSetReady:
		{
			Slot s = SlotOf(c);
			if (s != null)
			{
				s.Ready = r.ReadBoolean();
				lobbyDirty = true;
			}
			break;
		}
		case MsgPong:
		{
			Slot s2 = SlotOf(c);
			if (s2 != null)
			{
				s2.PingMs = Math.Max(0, Environment.TickCount - r.ReadInt32());
			}
			break;
		}
		case MsgBye:
		{
			Slot s3 = SlotOf(c);
			if (s3 != null)
			{
				slots[s3.Index] = null;
				lobbyDirty = true;
				Status = s3.Name + " saiu da sala";
				Fire(PlayerLeft, s3.Index);
			}
			Close(c);
			break;
		}
		}
	}

	private void HostHello(Connection c, BinaryReader r)
	{
		if (!pending.Contains(c))
		{
			return;
		}
		pending.Remove(c);
		int version = r.ReadInt32();
		string name = CleanName(r.ReadString());
		string helloToken = r.ReadString();
		if (version != ProtocolVersion)
		{
			Reject(c, "Versao do mod diferente (host " + ProtocolVersion + ", voce " + version + "). Usem a mesma DLL.");
			return;
		}
		Slot s = slots[1];
		bool resumed = false;
		if (s != null && helloToken.Length > 0 && helloToken == s.Token)
		{
			if (s.Conn != null && s.Conn != c)
			{
				Close(s.Conn);
			}
			resumed = true;
		}
		else if (s == null)
		{
			s = new Slot
			{
				Index = 1,
				Token = Guid.NewGuid().ToString("N"),
				Ready = (State == SessionState.InGame)
			};
			slots[1] = s;
		}
		else
		{
			Reject(c, (s.Conn == null) ? "A vaga esta reservada para quem caiu. Tente de novo em alguns segundos." : "A sala esta cheia");
			return;
		}
		s.Name = name;
		s.Conn = c;
		s.LostAt = 0f;
		c.Slot = 1;
		c.Welcomed = true;
		c.Enqueue(MsgWelcome, Build(delegate(BinaryWriter w)
		{
			w.Write(true);
			w.Write(string.Empty);
			w.Write(s.Token);
			w.Write(resumed);
			w.Write((byte)1);
		}));
		if (State == SessionState.InGame)
		{
			c.Enqueue(MsgStartGame, new byte[1] { (byte)Mode });
		}
		lobbyDirty = true;
		NetLog.Info("" + name + (resumed ? " voltou" : " entrou") + " (" + c.Remote + ")");
		if (resumed)
		{
			Status = name + " reconectou";
			Fire(PlayerReconnected, 1);
		}
		else
		{
			Status = name + " entrou na sala";
			Fire(PlayerJoined, 1);
		}
	}

	private void Reject(Connection c, string reason)
	{
		c.Enqueue(MsgWelcome, Build(delegate(BinaryWriter w)
		{
			w.Write(false);
			w.Write(reason);
			w.Write(string.Empty);
			w.Write(false);
			w.Write((byte)0);
		}));
		c.CloseAfterSend = true;
		c.Signal.Set();
	}

	private Slot SlotOf(Connection c)
	{
		int i = c.Slot;
		if (i > 0 && i < MaxPlayers && slots[i] != null && slots[i].Conn == c)
		{
			return slots[i];
		}
		return null;
	}

	private void HostClosed(Connection c)
	{
		pending.Remove(c);
		Slot s = SlotOf(c);
		if (s == null)
		{
			return;
		}
		s.Conn = null;
		s.LostAt = Time.unscaledTime;
		lobbyDirty = true;
		Status = s.Name + " caiu. Esperando reconectar...";
		NetLog.Info("" + s.Name + " caiu");
		Fire(PlayerDisconnected, s.Index);
	}

	private void HostUpdate()
	{
		float now = Time.unscaledTime;
		for (int i = pending.Count - 1; i >= 0; i--)
		{
			if (now - pending[i].OpenedAt > HelloTimeout)
			{
				Close(pending[i]);
				pending.RemoveAt(i);
			}
		}
		float reserve = (State == SessionState.InGame) ? GameReserveSeconds : LobbyReserveSeconds;
		for (int j = 1; j < MaxPlayers; j++)
		{
			Slot s = slots[j];
			if (s != null && s.Conn == null && now - s.LostAt > reserve)
			{
				slots[j] = null;
				lobbyDirty = true;
				Status = s.Name + " nao voltou; a vaga foi liberada";
				Fire(PlayerLeft, j);
			}
		}
		if (lobbyDirty)
		{
			lobbyDirty = false;
			byte[] state = BuildLobbyState();
			foreach (Connection c in Targets(-1))
			{
				c.Enqueue(MsgLobbyState, state);
			}
		}
		if (now >= nextBeacon)
		{
			nextBeacon = now + 1f;
			int count = 0;
			foreach (Slot s2 in slots)
			{
				if (s2 != null)
				{
					count++;
				}
			}
			LanDiscovery.SendBeacon(LocalName, Port, count, MaxPlayers, State == SessionState.InGame);
		}
	}

	private byte[] BuildLobbyState()
	{
		List<PlayerInfo> players = GetPlayers();
		return Build(delegate(BinaryWriter w)
		{
			w.Write((byte)State);
			w.Write((byte)Mode);
			w.Write((byte)players.Count);
			foreach (PlayerInfo p in players)
			{
				w.Write((byte)p.Slot);
				w.Write(p.Name);
				w.Write(p.Ready);
				w.Write(p.Connected);
				w.Write((short)Mathf.Min(p.PingMs, 32767));
				w.Write(p.ReserveLeft);
			}
		});
	}

	// ---------- amigo ----------

	// address: codigo de sala, ip ou ip:porta
	public bool Join(string address, int defaultPort)
	{
		if (!RoomCode.TryParseAddress(address, defaultPort, out var host, out var port))
		{
			Status = "Codigo ou IP invalido";
			return false;
		}
		Leave();
		generation++;
		CurrentRole = Role.Guest;
		State = SessionState.Connecting;
		LocalSlot = 1;
		JoinAddress = host;
		joinPort = port;
		token = string.Empty;
		gameStartedOnce = false;
		ReconnectAttempt = 0;
		remotePlayers.Clear();
		Status = "Conectando em " + host + ":" + port + "...";
		BeginConnect();
		return true;
	}

	public void SetReady(bool ready)
	{
		if (CurrentRole != Role.Guest || hostConn == null || !hostConn.Welcomed)
		{
			return;
		}
		hostConn.Enqueue(MsgSetReady, new byte[1] { (byte)(ready ? 1 : 0) });
		// Mostra na hora; o host confirma no proximo estado da sala
		foreach (PlayerInfo p in remotePlayers)
		{
			if (p.Slot == LocalSlot)
			{
				p.Ready = ready;
			}
		}
	}

	private void BeginConnect()
	{
		connecting = true;
		string host = JoinAddress;
		int port = joinPort;
		int gen = generation;
		Thread t = new Thread((ThreadStart)delegate
		{
			TcpClient tcp = new TcpClient();
			try
			{
				IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
				if (!ar.AsyncWaitHandle.WaitOne(5000))
				{
					tcp.Close();
					Post(new NetEvent
					{
						Kind = EventKind.ConnectFailed,
						Error = "tempo esgotado",
						Generation = gen
					});
					return;
				}
				tcp.EndConnect(ar);
				tcp.NoDelay = true;
				Connection c = new Connection
				{
					Client = tcp,
					Stream = tcp.GetStream(),
					Generation = gen,
					Slot = 0,
					LastReceive = Environment.TickCount,
					Remote = host + ":" + port
				};
				Post(new NetEvent
				{
					Kind = EventKind.Connected,
					Conn = c,
					Generation = gen
				});
			}
			catch (Exception ex)
			{
				tcp.Close();
				Post(new NetEvent
				{
					Kind = EventKind.ConnectFailed,
					Error = ex.Message,
					Generation = gen
				});
			}
		});
		t.IsBackground = true;
		t.Start();
	}

	private void GuestConnected(Connection c)
	{
		// connecting continua true ate o Welcome ou a queda, para nao abrir outra tentativa
		if (hostConn != null && hostConn.Closed == 0)
		{
			c.Client.Close();
			return;
		}
		hostConn = c;
		StartThreads(c);
		c.Enqueue(MsgHello, Build(delegate(BinaryWriter w)
		{
			w.Write(ProtocolVersion);
			w.Write(LocalName);
			w.Write(token);
		}));
		Status = (State == SessionState.Reconnecting) ? "Conectado de novo, voltando para a sala..." : "Entrando na sala...";
	}

	private void GuestConnectFailed(string error)
	{
		connecting = false;
		if (State == SessionState.Reconnecting)
		{
			nextAttempt = Time.unscaledTime + 2f;
			Status = "Reconectando (tentativa " + ReconnectAttempt + "): " + error;
		}
		else if (State == SessionState.Connecting)
		{
			EndSession("Nao conectou: " + error);
		}
	}

	private void GuestHandle(Connection c, byte type, byte[] payload)
	{
		if (c != hostConn)
		{
			return;
		}
		BinaryReader r = Reader(payload);
		switch (type)
		{
		case MsgWelcome:
		{
			bool ok = r.ReadBoolean();
			string reason = r.ReadString();
			string newToken = r.ReadString();
			bool resumed = r.ReadBoolean();
			int slot = r.ReadByte();
			if (!ok)
			{
				EndSession(reason);
				return;
			}
			bool wasReconnecting = State == SessionState.Reconnecting;
			connecting = false;
			token = newToken;
			LocalSlot = slot;
			c.Welcomed = true;
			ReconnectAttempt = 0;
			if (wasReconnecting && resumed)
			{
				State = gameStartedOnce ? SessionState.InGame : SessionState.Lobby;
				Status = "Reconectado!";
				Fire(PlayerReconnected, 0);
			}
			else
			{
				State = SessionState.Lobby;
				gameStartedOnce = false;
				Status = "Na sala. Marque pronto quando quiser comecar.";
			}
			break;
		}
		case MsgLobbyState:
		{
			SessionState hostState = (SessionState)r.ReadByte();
			Mode = (GameMode)r.ReadByte();
			int count = r.ReadByte();
			remotePlayers.Clear();
			for (int i = 0; i < count; i++)
			{
				PlayerInfo p = new PlayerInfo();
				p.Slot = r.ReadByte();
				p.Name = r.ReadString();
				p.Ready = r.ReadBoolean();
				p.Connected = r.ReadBoolean();
				p.PingMs = r.ReadInt16();
				p.ReserveLeft = r.ReadSingle();
				if (p.Slot == 0)
				{
					p.PingMs = hostPing;
				}
				remotePlayers.Add(p);
			}
			if (hostState == SessionState.Lobby && State == SessionState.InGame)
			{
				State = SessionState.Lobby;
			}
			break;
		}
		case MsgStartGame:
			if (payload.Length > 0)
			{
				Mode = (GameMode)payload[0];
			}
			State = SessionState.InGame;
			Status = "Partida iniciada";
			if (!gameStartedOnce)
			{
				FireGameStarted();
			}
			break;
		case MsgPong:
			hostPing = Math.Max(0, Environment.TickCount - r.ReadInt32());
			break;
		case MsgBye:
		{
			string reason2 = r.ReadString();
			EndSession(string.IsNullOrEmpty(reason2) ? "O host fechou a sala" : reason2);
			break;
		}
		}
	}

	private void GuestClosed(Connection c)
	{
		if (c != hostConn)
		{
			return;
		}
		hostConn = null;
		connecting = false;
		if (State == SessionState.Offline)
		{
			return;
		}
		if (State == SessionState.Reconnecting)
		{
			// Conectou mas caiu antes de voltar para a sala: espera um pouco e tenta de novo
			nextAttempt = Time.unscaledTime + 2f;
			return;
		}
		if (token.Length == 0)
		{
			EndSession("A conexao com o host caiu");
			return;
		}
		State = SessionState.Reconnecting;
		reconnectDeadline = Time.unscaledTime + ReconnectSeconds;
		nextAttempt = Time.unscaledTime + 1f;
		Status = "A conexao caiu. Reconectando...";
		NetLog.Info("conexao com o host caiu, reconectando");
		Fire(PlayerDisconnected, 0);
	}

	private void GuestUpdate()
	{
		if (State != SessionState.Reconnecting)
		{
			return;
		}
		if (Time.unscaledTime > reconnectDeadline)
		{
			EndSession("Nao consegui reconectar ao host");
		}
		else if (!connecting && Time.unscaledTime >= nextAttempt)
		{
			ReconnectAttempt++;
			Status = "Reconectando (tentativa " + ReconnectAttempt + ")...";
			BeginConnect();
		}
	}

	// ---------- comum ----------

	public void Leave()
	{
		if (CurrentRole == Role.None && State == SessionState.Offline)
		{
			return;
		}
		bool wasInSession = State != SessionState.Offline;
		generation++;
		if (CurrentRole == Role.Host)
		{
			listening = false;
			if (listener != null)
			{
				try
				{
					listener.Stop();
				}
				catch (Exception)
				{
				}
				listener = null;
			}
			for (int i = 1; i < MaxPlayers; i++)
			{
				if (slots[i] != null && slots[i].Conn != null)
				{
					SendBye(slots[i].Conn, "O host fechou a sala");
				}
				slots[i] = null;
			}
			slots[0] = null;
			foreach (Connection c in pending)
			{
				Close(c);
			}
			pending.Clear();
			NatHelper.Close();
			LanDiscovery.StopBeacon();
		}
		else if (CurrentRole == Role.Guest && hostConn != null)
		{
			SendBye(hostConn, string.Empty);
			hostConn = null;
		}
		CurrentRole = Role.None;
		State = SessionState.Offline;
		connecting = false;
		remotePlayers.Clear();
		if (wasInSession)
		{
			Status = "Voce saiu do online";
			Fire(SessionEnded, Status);
		}
	}

	private void EndSession(string reason)
	{
		if (hostConn != null)
		{
			Close(hostConn);
			hostConn = null;
		}
		generation++;
		CurrentRole = Role.None;
		State = SessionState.Offline;
		connecting = false;
		remotePlayers.Clear();
		Status = reason;
		NetLog.Info("sessao encerrada: " + reason);
		Fire(SessionEnded, reason);
	}

	private void SendBye(Connection c, string reason)
	{
		c.Enqueue(MsgBye, Build(delegate(BinaryWriter w)
		{
			w.Write(reason);
		}));
		c.CloseAfterSend = true;
		c.Signal.Set();
	}

	private void Update()
	{
		ProcessInbox();
		if (CurrentRole == Role.Host)
		{
			HostUpdate();
		}
		else if (CurrentRole == Role.Guest)
		{
			GuestUpdate();
		}
		if (Time.unscaledTime >= nextHeartbeat)
		{
			nextHeartbeat = Time.unscaledTime + 1f;
			Heartbeat();
		}
	}

	private void Heartbeat()
	{
		int now = Environment.TickCount;
		byte[] ping = Build(delegate(BinaryWriter w)
		{
			w.Write(now);
		});
		foreach (Connection c in AllConnections())
		{
			if (now - c.LastReceive > TimeoutMs)
			{
				NetLog.Info("sem resposta de " + c.Remote + ", fechando");
				Close(c);
			}
			else if (c.Welcomed)
			{
				c.Enqueue(MsgPing, ping);
			}
		}
		if (CurrentRole == Role.Host && slots[1] != null)
		{
			lobbyDirty = true;
		}
	}

	private void ProcessInbox()
	{
		while (true)
		{
			NetEvent e;
			lock (inboxLock)
			{
				if (inbox.Count == 0)
				{
					return;
				}
				e = inbox.Dequeue();
			}
			try
			{
				Dispatch(e);
			}
			catch (Exception ex)
			{
				NetLog.Info("ERRO erro tratando mensagem " + e.Type + ": " + ex);
			}
		}
	}

	private void Dispatch(NetEvent e)
	{
		bool current = e.Generation == generation;
		switch (e.Kind)
		{
		case EventKind.Accepted:
			if (!current || CurrentRole != Role.Host)
			{
				e.Conn.Client.Close();
				break;
			}
			e.Conn.OpenedAt = Time.unscaledTime;
			pending.Add(e.Conn);
			StartThreads(e.Conn);
			break;
		case EventKind.Connected:
			if (!current || CurrentRole != Role.Guest)
			{
				e.Conn.Client.Close();
				break;
			}
			GuestConnected(e.Conn);
			break;
		case EventKind.ConnectFailed:
			if (current && CurrentRole == Role.Guest)
			{
				GuestConnectFailed(e.Error);
			}
			break;
		case EventKind.Closed:
			if (CurrentRole == Role.Host)
			{
				HostClosed(e.Conn);
			}
			else if (CurrentRole == Role.Guest)
			{
				GuestClosed(e.Conn);
			}
			break;
		case EventKind.Message:
			if (e.Type <= LastSessionType)
			{
				if (CurrentRole == Role.Host)
				{
					HostHandle(e.Conn, e.Type, e.Payload);
				}
				else if (CurrentRole == Role.Guest)
				{
					GuestHandle(e.Conn, e.Type, e.Payload);
				}
			}
			else if (e.Conn.Welcomed && e.Conn.Closed == 0)
			{
				MessageHandler handler = mainHandlers[e.Type];
				if (handler != null)
				{
					handler(e.Conn.Slot, e.Payload);
				}
			}
			break;
		}
	}

	private void Post(NetEvent e)
	{
		lock (inboxLock)
		{
			inbox.Enqueue(e);
		}
	}

	private List<Connection> Targets(int toSlot)
	{
		List<Connection> list = new List<Connection>();
		if (CurrentRole == Role.Host)
		{
			for (int i = 1; i < MaxPlayers; i++)
			{
				Slot s = slots[i];
				Connection c = (s != null) ? s.Conn : null;
				if (c != null && c.Welcomed && (toSlot < 0 || toSlot == i))
				{
					list.Add(c);
				}
			}
		}
		else if (CurrentRole == Role.Guest)
		{
			Connection c2 = hostConn;
			if (c2 != null && c2.Welcomed && (toSlot < 0 || toSlot == 0))
			{
				list.Add(c2);
			}
		}
		return list;
	}

	private List<Connection> AllConnections()
	{
		List<Connection> list = new List<Connection>(pending);
		if (CurrentRole == Role.Host)
		{
			for (int i = 1; i < MaxPlayers; i++)
			{
				if (slots[i] != null && slots[i].Conn != null)
				{
					list.Add(slots[i].Conn);
				}
			}
		}
		else if (hostConn != null)
		{
			list.Add(hostConn);
		}
		return list;
	}

	private void StartThreads(Connection c)
	{
		Thread read = new Thread((ThreadStart)delegate
		{
			ReadLoop(c);
		});
		read.IsBackground = true;
		read.Start();
		Thread send = new Thread((ThreadStart)delegate
		{
			SendLoop(c);
		});
		send.IsBackground = true;
		send.Start();
	}

	private void ReadLoop(Connection c)
	{
		try
		{
			BinaryReader r = new BinaryReader(c.Stream);
			while (c.Closed == 0)
			{
				byte type = r.ReadByte();
				int len = r.ReadInt32();
				if (len < 0 || len > MaxMessageBytes)
				{
					throw new IOException("mensagem invalida");
				}
				byte[] payload = (len == 0) ? Empty : r.ReadBytes(len);
				if (payload.Length != len)
				{
					throw new EndOfStreamException();
				}
				c.LastReceive = Environment.TickCount;
				if (type == MsgPing)
				{
					// Responde aqui mesmo para o ping medir so a rede
					c.Enqueue(MsgPong, payload);
					continue;
				}
				if (type > LastSessionType && c.Welcomed)
				{
					MessageHandler handler = threadHandlers[type];
					if (handler != null)
					{
						try
						{
							handler(c.Slot, payload);
						}
						catch (Exception ex)
						{
							NetLog.Info("ERRO erro no handler do tipo " + type + ": " + ex);
						}
						continue;
					}
				}
				Post(new NetEvent
				{
					Kind = EventKind.Message,
					Conn = c,
					Type = type,
					Payload = payload,
					Generation = c.Generation
				});
			}
		}
		catch (Exception ex2)
		{
			if (c.Closed == 0)
			{
				NetLog.Info("leitura encerrada (" + c.Remote + "): " + ex2.Message);
			}
		}
		Close(c);
	}

	private void SendLoop(Connection c)
	{
		List<byte[]> batch = new List<byte[]>();
		try
		{
			while (c.Closed == 0)
			{
				c.Signal.WaitOne(100);
				lock (c.Queue)
				{
					while (c.Queue.Count > 0)
					{
						batch.Add(c.Queue.Dequeue());
					}
					foreach (byte[] value in c.Latest.Values)
					{
						batch.Add(value);
					}
					c.Latest.Clear();
				}
				if (batch.Count > 0)
				{
					foreach (byte[] b in batch)
					{
						c.Stream.Write(b, 0, b.Length);
					}
					c.Stream.Flush();
					batch.Clear();
				}
				else if (c.CloseAfterSend)
				{
					break;
				}
			}
		}
		catch (Exception ex)
		{
			if (c.Closed == 0)
			{
				NetLog.Info("envio encerrado (" + c.Remote + "): " + ex.Message);
			}
		}
		Close(c);
	}

	private void Close(Connection c)
	{
		if (Interlocked.Exchange(ref c.Closed, 1) == 1)
		{
			return;
		}
		try
		{
			c.Client.Close();
		}
		catch (Exception)
		{
		}
		c.Signal.Set();
		Post(new NetEvent
		{
			Kind = EventKind.Closed,
			Conn = c,
			Generation = c.Generation
		});
	}

	private void FireGameStarted()
	{
		gameStartedOnce = true;
		NetLog.Info("partida iniciada (modo " + Mode + ")");
		if (GameStarted != null)
		{
			try
			{
				GameStarted();
			}
			catch (Exception ex)
			{
				NetLog.Info("ERRO " + ex);
			}
		}
	}

	private static void Fire<T>(Action<T> handler, T arg)
	{
		if (handler == null)
		{
			return;
		}
		try
		{
			handler(arg);
		}
		catch (Exception ex)
		{
			NetLog.Info("ERRO " + ex);
		}
	}

	private static string CleanName(string name)
	{
		name = (name ?? string.Empty).Trim();
		if (name.Length > 20)
		{
			name = name.Substring(0, 20);
		}
		return (name.Length == 0) ? "Jogador" : name;
	}

	private static byte[] Frame(byte type, byte[] payload)
	{
		byte[] framed = new byte[5 + payload.Length];
		framed[0] = type;
		framed[1] = (byte)payload.Length;
		framed[2] = (byte)(payload.Length >> 8);
		framed[3] = (byte)(payload.Length >> 16);
		framed[4] = (byte)(payload.Length >> 24);
		Buffer.BlockCopy(payload, 0, framed, 5, payload.Length);
		return framed;
	}

	public static byte[] Build(Action<BinaryWriter> write)
	{
		using (MemoryStream ms = new MemoryStream())
		{
			BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8);
			write(w);
			w.Flush();
			return ms.ToArray();
		}
	}

	public static BinaryReader Reader(byte[] payload)
	{
		return new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
	}
}
