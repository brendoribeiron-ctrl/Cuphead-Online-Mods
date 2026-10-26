using System;
using System.Collections.Generic;
using UnityEngine;

// MOD ONLINE: tela do lobby (F1). Criar sala, entrar por codigo, salas da rede local,
// lista de jogadores, pronto/iniciar e reconexao. A conexao fica no NetSession.
//
// Linha de comando:
//   -name <nome>  -port <porta>  -host  -join <codigo | ip | ip:porta>
//   -mode stream|sync  -autoready (amigo marca pronto sozinho)  -autostart (host inicia
//   quando o amigo ficar pronto)  -nolobby (comeca com a janela fechada)
public class OnlineLobby : MonoBehaviour
{
	private const string PrefName = "OnlineName";

	private const string PrefPort = "OnlinePort";

	private const string PrefJoin = "OnlineLastJoin";

	public static OnlineLobby Instance { get; private set; }

	private NetSession session;

	private bool showWindow = true;

	public bool WindowVisible => showWindow;

	private Rect windowRect = new Rect(30f, 110f, 520f, 440f);

	private Vector2 scroll;

	private string nameText = "Jogador";

	private string portText = "7777";

	private string joinText = string.Empty;

	private bool showAdvanced;

	private bool autoReady;

	private bool autoStart;

	// -name/-port na linha de comando (testes): nao grava nas preferencias, que sao as mesmas do jogo de verdade
	private bool prefsFromArgs;

	private bool readySent;

	private string copiedNote = string.Empty;

	private float copiedUntil;

	private List<string> localIps = new List<string>();

	private void Awake()
	{
		Instance = this;
		session = base.gameObject.AddComponent<NetSession>();
		base.gameObject.AddComponent<OnlineStream>();
		base.gameObject.AddComponent<NetcodeBridge>();
		string fallbackName = "Jogador";
		try
		{
			fallbackName = Environment.UserName;
		}
		catch (Exception)
		{
		}
		nameText = PlayerPrefs.GetString(PrefName, fallbackName);
		portText = PlayerPrefs.GetInt(PrefPort, NetSession.DefaultPort).ToString();
		joinText = PlayerPrefs.GetString(PrefJoin, string.Empty);
		localIps = LanDiscovery.LocalAddresses();
		session.GameStarted += delegate
		{
			showWindow = false;
		};
		session.SessionEnded += delegate
		{
			showWindow = true;
			readySent = false;
		};
	}

	private void Start()
	{
		string[] args = Environment.GetCommandLineArgs();
		bool host = false;
		string join = null;
		for (int i = 0; i < args.Length; i++)
		{
			string a = args[i].ToLowerInvariant();
			string next = (i + 1 < args.Length) ? args[i + 1] : null;
			switch (a)
			{
			case "-host":
				host = true;
				break;
			case "-join":
				if (next != null)
				{
					join = next;
					i++;
				}
				break;
			case "-name":
				if (next != null)
				{
					prefsFromArgs = true;
					nameText = next;
					i++;
				}
				break;
			case "-port":
				if (next != null)
				{
					prefsFromArgs = true;
					portText = next;
					i++;
				}
				break;
			case "-mode":
				if (next != null)
				{
					NetSession.DefaultMode = (next.ToLowerInvariant() == "sync") ? NetSession.GameMode.Sync : NetSession.GameMode.Stream;
					i++;
				}
				break;
			case "-autoready":
				autoReady = true;
				break;
			case "-autostart":
				autoStart = true;
				break;
			case "-nolobby":
				showWindow = false;
				break;
			}
		}
		if (host)
		{
			CreateRoom();
		}
		else if (join != null)
		{
			joinText = join;
			JoinRoom();
		}
	}

	private void Update()
	{
		if (Input.GetKeyDown(KeyCode.F1))
		{
			showWindow = !showWindow;
		}
		// Online: o jogo nao pode pausar quando a janela perde o foco
		Application.runInBackground = session.InSession || NetcodeSync.Active || NetcodeTestTransport.Started;
		bool wantLan = showWindow && !session.InSession;
		if (wantLan && !LanDiscovery.Listening)
		{
			LanDiscovery.StartListening();
		}
		else if (!wantLan && LanDiscovery.Listening)
		{
			LanDiscovery.StopListening();
		}
		if (session.IsGuest && session.State == NetSession.SessionState.Lobby)
		{
			if (autoReady && !readySent && session.PeerConnected)
			{
				readySent = true;
				session.SetReady(ready: true);
			}
		}
		else if (session.State != NetSession.SessionState.Reconnecting && session.State != NetSession.SessionState.InGame)
		{
			readySent = false;
		}
		if (autoStart && session.CanStart)
		{
			session.StartGame();
		}
	}

	private int ParsePort()
	{
		if (int.TryParse(portText, out var port) && port > 0 && port < 65536)
		{
			return port;
		}
		return NetSession.DefaultPort;
	}

	private void ApplyName()
	{
		string name = nameText.Trim();
		if (name.Length == 0)
		{
			name = "Jogador";
		}
		session.LocalName = name;
		if (prefsFromArgs)
		{
			return;
		}
		PlayerPrefs.SetString(PrefName, name);
		PlayerPrefs.SetInt(PrefPort, ParsePort());
		PlayerPrefs.Save();
	}

	private void CreateRoom()
	{
		ApplyName();
		session.Host(ParsePort());
	}

	private void JoinRoom()
	{
		ApplyName();
		if (session.Join(joinText, ParsePort()) && !prefsFromArgs)
		{
			PlayerPrefs.SetString(PrefJoin, joinText.Trim());
			PlayerPrefs.Save();
		}
	}

	private void JoinLan(LanDiscovery.Room room)
	{
		ApplyName();
		session.Join(room.Address + ":" + room.Port, room.Port);
	}

	private void Copy(string text, string what)
	{
		GUIUtility.systemCopyBuffer = text;
		copiedNote = what + " copiado!";
		copiedUntil = Time.unscaledTime + 2f;
	}

	// ---------- tela ----------

	private void OnGUI()
	{
		float scale = Mathf.Max(1f, (float)Screen.height / 720f);
		Matrix4x4 matrix = GUI.matrix;
		GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
		GUI.depth = -1010;
		if (session.IsHost)
		{
			string bar = session.PeerConnected ? ("ONLINE: amigo conectado no player 2  |  " + session.PingMs + " ms  |  " + ((NetInput.LatestButtons != 0) ? "amigo apertando botao" : "amigo parado")) : ((session.GetPlayers().Count > 1) ? "ONLINE: amigo caiu, esperando ele voltar" : "ONLINE: esperando amigo");
			// No topo: no rodape fica a barra do amigo, e esta tambem vai na imagem transmitida
			GUI.Label(new Rect(10f, 4f, 900f, 30f), bar + "  |  F1 = lobby" + ((session.State == NetSession.SessionState.InGame && session.Mode == NetSession.GameMode.Stream && showWindow) ? "  |  imagem pausada com o lobby aberto" : string.Empty));
		}
		else if (session.State == NetSession.SessionState.Reconnecting)
		{
			GUI.Label(new Rect(10f, (float)Screen.height / scale - 55f, 900f, 30f), "ONLINE: reconectando ao host... " + Mathf.CeilToInt(session.ReconnectTimeLeft) + "s");
		}
		if (showWindow)
		{
			windowRect = GUI.Window(77001, windowRect, DrawWindow, "Lobby Online (F1 mostra/esconde)");
		}
		GUI.matrix = matrix;
	}

	private void DrawWindow(int id)
	{
		GUILayout.Space(4f);
		GUILayout.Label("Status: " + session.Status);
		if (Time.unscaledTime < copiedUntil)
		{
			GUILayout.Label(copiedNote);
		}
		GUILayout.Space(4f);
		scroll = GUILayout.BeginScrollView(scroll);
		if (!session.InSession)
		{
			DrawOffline();
		}
		else if (session.IsHost)
		{
			DrawHost();
		}
		else
		{
			DrawGuest();
		}
		GUILayout.EndScrollView();
		GUI.DragWindow();
	}

	private void DrawOffline()
	{
		GUILayout.BeginHorizontal();
		GUILayout.Label("Seu nome:", GUILayout.Width(80f));
		nameText = GUILayout.TextField(nameText, 20);
		GUILayout.EndHorizontal();
		GUILayout.Space(6f);
		if (GUILayout.Button("Criar sala", GUILayout.Height(32f)))
		{
			CreateRoom();
		}
		GUILayout.Label("Voce joga como player 1 e recebe um codigo para passar ao amigo.");
		GUILayout.Space(8f);
		GUILayout.Label("Salas na sua rede:");
		List<LanDiscovery.Room> rooms = LanDiscovery.GetRooms();
		if (rooms.Count == 0)
		{
			GUILayout.Label("   (nenhuma sala encontrada ainda)");
		}
		foreach (LanDiscovery.Room room in rooms)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label("   " + room.HostName + "  (" + room.Players + "/" + room.MaxPlayers + ((room.InGame) ? ", jogando" : string.Empty) + ")  " + room.Address);
			GUI.enabled = room.Players < room.MaxPlayers;
			if (GUILayout.Button("Entrar", GUILayout.Width(80f)))
			{
				JoinLan(room);
			}
			GUI.enabled = true;
			GUILayout.EndHorizontal();
		}
		GUILayout.Space(8f);
		GUILayout.Label("Entrar com codigo da sala (ou IP):");
		GUILayout.BeginHorizontal();
		joinText = GUILayout.TextField(joinText, 64);
		if (GUILayout.Button("Colar", GUILayout.Width(60f)))
		{
			joinText = GUIUtility.systemCopyBuffer.Trim();
		}
		GUILayout.EndHorizontal();
		if (GUILayout.Button("Entrar na sala", GUILayout.Height(30f)))
		{
			JoinRoom();
		}
		GUILayout.Space(6f);
		showAdvanced = GUILayout.Toggle(showAdvanced, " Avancado");
		if (showAdvanced)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label("Porta:", GUILayout.Width(60f));
			portText = GUILayout.TextField(portText, 6, GUILayout.Width(80f));
			GUILayout.EndHorizontal();
			GUILayout.Label("Seus IPs: " + string.Join(", ", localIps.ToArray()));
		}
	}

	private void DrawHost()
	{
		int port = session.Port;
		string publicIp = NatHelper.PublicIp;
		if (publicIp != null)
		{
			string code = RoomCode.Encode(System.Net.IPAddress.Parse(publicIp), port);
			GUILayout.BeginHorizontal();
			GUILayout.Label("Codigo da sala (internet):  " + code);
			if (GUILayout.Button("Copiar", GUILayout.Width(70f)))
			{
				Copy(code, "Codigo");
			}
			GUILayout.EndHorizontal();
		}
		else if (NatHelper.Busy)
		{
			GUILayout.Label("Preparando o codigo da internet...");
		}
		if (NatHelper.Status.Length > 0)
		{
			GUILayout.Label(NatHelper.Status);
		}
		foreach (string ip in localIps)
		{
			string lanCode = RoomCode.Encode(System.Net.IPAddress.Parse(ip), port);
			GUILayout.BeginHorizontal();
			GUILayout.Label("Rede local/VPN (" + ip + "):  " + lanCode);
			if (GUILayout.Button("Copiar", GUILayout.Width(70f)))
			{
				Copy(lanCode, "Codigo");
			}
			GUILayout.EndHorizontal();
		}
		GUILayout.Space(6f);
		if (session.State == NetSession.SessionState.Lobby)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label("Modo:", GUILayout.Width(50f));
			int mode = GUILayout.Toolbar((session.Mode == NetSession.GameMode.Stream) ? 0 : 1, new string[2] { "Tela compartilhada", "Sincronizado" });
			session.SetMode((mode == 0) ? NetSession.GameMode.Stream : NetSession.GameMode.Sync);
			GUILayout.EndHorizontal();
		}
		DrawPlayers(canKick: true);
		GUILayout.Space(6f);
		if (session.State == NetSession.SessionState.Lobby)
		{
			GUI.enabled = session.CanStart;
			if (GUILayout.Button(session.CanStart ? "Iniciar partida" : "Esperando o amigo ficar pronto...", GUILayout.Height(32f)))
			{
				session.StartGame();
			}
			GUI.enabled = true;
		}
		else
		{
			GUILayout.Label("Partida em andamento (modo " + ModeName(session.Mode) + ").");
		}
		if (GUILayout.Button("Fechar sala", GUILayout.Height(26f)))
		{
			session.Leave();
		}
	}

	private void DrawGuest()
	{
		switch (session.State)
		{
		case NetSession.SessionState.Connecting:
			GUILayout.Label("Conectando em " + session.JoinAddress + "...");
			break;
		case NetSession.SessionState.Reconnecting:
			GUILayout.Label("A conexao caiu. Tentando voltar sozinho por mais " + Mathf.CeilToInt(session.ReconnectTimeLeft) + "s.");
			break;
		case NetSession.SessionState.Lobby:
		{
			GUILayout.Label("Modo da partida: " + ModeName(session.Mode));
			bool ready = session.LocalReady;
			if (GUILayout.Button(ready ? "Pronto! (clique para cancelar)" : "Estou pronto", GUILayout.Height(32f)))
			{
				session.SetReady(!ready);
			}
			GUILayout.Label(ready ? "Esperando o host iniciar." : "Marque pronto quando quiser comecar.");
			break;
		}
		case NetSession.SessionState.InGame:
			GUILayout.Label("Partida em andamento (modo " + ModeName(session.Mode) + ").");
			if (session.Mode == NetSession.GameMode.Stream)
			{
				GUILayout.Label("Seus controles vao para o player 2 do host.");
			}
			break;
		}
		if (session.State != NetSession.SessionState.Connecting)
		{
			DrawPlayers(canKick: false);
		}
		GUILayout.Space(6f);
		if (GUILayout.Button((session.State == NetSession.SessionState.Reconnecting || session.State == NetSession.SessionState.Connecting) ? "Cancelar" : "Sair da sala", GUILayout.Height(26f)))
		{
			session.Leave();
		}
	}

	private void DrawPlayers(bool canKick)
	{
		GUILayout.Label("Jogadores:");
		List<NetSession.PlayerInfo> players = session.GetPlayers();
		for (int slot = 0; slot < NetSession.MaxPlayers; slot++)
		{
			NetSession.PlayerInfo p = null;
			foreach (NetSession.PlayerInfo item in players)
			{
				if (item.Slot == slot)
				{
					p = item;
				}
			}
			GUILayout.BeginHorizontal();
			if (p == null)
			{
				GUILayout.Label("   P" + (slot + 1) + ":  (vaga livre)");
			}
			else
			{
				string state;
				if (!p.Connected)
				{
					state = "caiu, vaga guardada por " + Mathf.CeilToInt(p.ReserveLeft) + "s";
				}
				else if (slot == 0)
				{
					state = "host";
				}
				else
				{
					state = (p.Ready ? "pronto" : "nao pronto") + "  |  " + p.PingMs + " ms";
				}
				GUILayout.Label("   P" + (slot + 1) + ":  " + p.Name + ((slot == session.LocalSlot) ? " (voce)" : string.Empty) + "  -  " + state);
				if (canKick && slot != 0 && GUILayout.Button("Remover", GUILayout.Width(80f)))
				{
					session.Kick(slot);
				}
			}
			GUILayout.EndHorizontal();
		}
	}

	private static string ModeName(NetSession.GameMode mode)
	{
		return (mode == NetSession.GameMode.Stream) ? "tela compartilhada" : "sincronizado";
	}
}
