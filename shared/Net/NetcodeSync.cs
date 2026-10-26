using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Rewired;
using UnityEngine;
using UnityEngine.SceneManagement;

// MOD ONLINE: motor do lockstep. Ver NetProtocol.cs para o contrato com o lobby.
//
// Cada frame do Unity vira exatamente um frame do lockstep (Advance). No Advance este PC
// le o proprio controle, manda para o outro (para daqui a InputDelay frames) e espera ate
// ter o controle do outro para o frame atual. Os dois jogos recebem os mesmos controles
// (player 1 = host, player 2 = amigo), com deltaTime fixo e a mesma semente de Random.
public class NetcodeSync : MonoBehaviour
{
	private struct Snap
	{
		public uint hash;

		public NetProtocol.PlayerState[] players;

		public float bossDamage;
	}

	private struct PendingGo
	{
		public string key;

		public int frame;

		public int seed;
	}

	private const int BufferSize = 1024;

	private const float StallTimeout = 10f;

	private const float BarrierTimeout = 60f;

	private const int SnapshotInterval = 30;

	private const int TargetFps = 60;

	public static NetcodeSync Instance { get; private set; }

	public static bool Active { get; private set; }

	public static bool IsHost { get; private set; }

	public static bool InLockstep => running;

	public static int Frame => frame;

	public static int InputDelay => inputDelay;

	public static int DesyncCount { get; private set; }

	public static string Status { get; private set; }

	private static readonly object sync = new object();

	private static INetcodeTransport transport;

	private static volatile bool running;

	private static bool pendingBegin;

	private static bool pendingEnd;

	private static string pendingEndReason;

	private static bool helloSent;

	private static bool peerHello;

	private static readonly Queue<KeyValuePair<byte, byte[]>> mainQueue = new Queue<KeyValuePair<byte, byte[]>>();

	private static readonly NetProtocol.InputFrame[] localBuf = new NetProtocol.InputFrame[BufferSize];

	private static readonly NetProtocol.InputFrame[] remoteBuf = new NetProtocol.InputFrame[BufferSize];

	private static readonly int[] remoteTag = new int[BufferSize];

	private static int frame;

	// numero da partida: muda a cada (re)inicio do lockstep
	private static byte epoch;

	private static int inputDelay = NetProtocol.DefaultInputDelay;

	private static int lastAdvanceUnityFrame = -1;

	private static int savedVSync = -1;

	private static int savedTargetFps = 60;

	private static float savedFixedDelta = 0.02f;

	private static bool advancing;

	private static readonly HashSet<string> peerReady = new HashSet<string>();

	private static PendingGo? pendingGo;

	private static string[] deferredScene;

	private static readonly Dictionary<int, Snap> localSnaps = new Dictionary<int, Snap>();

	private static readonly Dictionary<int, Snap> hostSnaps = new Dictionary<int, Snap>();

	private static readonly float[] axesTmp = new float[4];

	private static long pingMs = -1;

	// Estatisticas de rede para o log (a cada 10 s): ping e quanto o jogo ficou esperando o outro PC
	private static readonly Queue<long> recentPings = new Queue<long>();

	private static long statPingMin = long.MaxValue;

	private static long statPingMax;

	private static long statPingSum;

	private static int statPingCount;

	private static int statStalls;

	private static double statStallMs;

	private static double statStallMax;

	private float nextStats;

	private float nextPing;

	// Opcoes de teste (linha de comando)
	private static bool delayFromArgs;

	private static int autoSlot = -1;

	private static bool autoTitleDone;

	private static bool autoEntered;

	private static System.Random bot;

	private static Levels? testLevel;

	private static NetProtocol.InputFrame botFrame;

	private static int botNextChange;

	// pulo, tiro, super, trocar arma, travar, dash, aceitar
	private static readonly int[] BotButtons = new int[7] { 2, 3, 4, 5, 6, 7, 13 };

	// -netcode-testwin N: o chefe morre N frames depois de a fase comecar (testa a tela de vitoria)
	private static int testWinAfter = -1;

	private static int testDesyncAfter = -1;

	private static Level testWinLevel;

	private static int testWinStart;

	// Amigo carregando uma cena a pedido do host (as outras trocas de cena dele sao ignoradas)
	private static bool clientLoading;

	private static float lastResync = -100f;

	private const float ResyncInterval = 10f;

	private static int severeStreak;

	static NetcodeSync()
	{
		Status = string.Empty;
	}

	// ---------- API para o lobby ----------

	public static void Begin(INetcodeTransport t, bool isHost)
	{
		lock (sync)
		{
			transport = t;
			IsHost = isHost;
			Active = true;
			pendingBegin = true;
			// Reconexao com o lockstep ainda rodando: encerra a partida velha antes de comecar outra
			pendingEnd = running;
			pendingEndReason = "reconectando";
			mainQueue.Clear();
		}
	}

	public static void End()
	{
		End("conexao encerrada");
	}

	public static void End(string reason)
	{
		lock (sync)
		{
			if (!Active)
			{
				return;
			}
			pendingEnd = true;
			pendingEndReason = reason;
			Monitor.PulseAll(sync);
		}
	}

	// Pode ser chamado de qualquer thread
	public static void OnPacket(byte type, byte[] payload)
	{
		try
		{
			switch (type)
			{
			case NetProtocol.Input:
			{
				BinaryReader binaryReader = NetProtocol.Reader(payload);
				byte b = binaryReader.ReadByte();
				int num = binaryReader.ReadInt32();
				int num2 = binaryReader.ReadByte();
				lock (sync)
				{
					// inputs de uma partida anterior (antes de recomecar) sao ignorados
					if (b != epoch)
					{
						break;
					}
					for (int i = 0; i < num2; i++)
					{
						int num3 = num + i;
						remoteBuf[num3 % BufferSize] = NetProtocol.InputFrame.Read(binaryReader);
						remoteTag[num3 % BufferSize] = num3;
					}
					Monitor.PulseAll(sync);
				}
				break;
			}
			case NetProtocol.Config:
				// Ja na thread de rede: os inputs do host que vem logo depois sao desta partida
				lock (sync)
				{
					if (!IsHost && payload.Length > 0)
					{
						epoch = payload[0];
						ClearRemote();
					}
					mainQueue.Enqueue(new KeyValuePair<byte, byte[]>(type, payload));
				}
				break;
			case NetProtocol.Ping:
				Send(NetProtocol.Pong, payload);
				break;
			case NetProtocol.Pong:
			{
				long num4 = NetProtocol.Reader(payload).ReadInt64();
				pingMs = (DateTime.UtcNow.Ticks - num4) / 10000;
				lock (sync)
				{
					recentPings.Enqueue(pingMs);
					while (recentPings.Count > 15)
					{
						recentPings.Dequeue();
					}
					statPingMin = Math.Min(statPingMin, pingMs);
					statPingMax = Math.Max(statPingMax, pingMs);
					statPingSum += pingMs;
					statPingCount++;
				}
				break;
			}
			default:
				lock (sync)
				{
					mainQueue.Enqueue(new KeyValuePair<byte, byte[]>(type, payload));
				}
				break;
			}
		}
		catch (Exception ex)
		{
			NetLog.Info("[Netcode] pacote invalido " + type + ": " + ex.Message);
		}
	}

	// ---------- Ganchos do jogo ----------

	// SceneLoader.LoadScene: o host avisa o amigo de toda troca de cena
	public static void OnSceneLoadStarted(Scenes scene, SceneLoader.Transition transitionStart, SceneLoader.Transition transitionEnd, SceneLoader.Icon icon)
	{
		if (!running)
		{
			return;
		}
		NetLog.Info("[Netcode] carregando " + scene + " no frame " + frame);
		if (!IsHost)
		{
			return;
		}
		string name = scene.ToString();
		lock (sync)
		{
			// O amigo pode ter chegado antes nesta mesma cena (ele ja mandou o "pronto"): esse aviso fica
			peerReady.RemoveWhere((string k) => !k.EndsWith(":" + name));
		}
		int level = (int)SceneLoader.CurrentLevel;
		Send(NetProtocol.SceneLoad, NetProtocol.Build(delegate(BinaryWriter w)
		{
			w.Write(name);
			w.Write(level);
			w.Write((byte)transitionStart);
			w.Write((byte)transitionEnd);
			w.Write((byte)icon);
		}));
	}

	// SceneLoader.LoadScene: no lockstep so o host decide as trocas de cena. Se o jogo do amigo se
	// separou (ex.: ele acha que os dois morreram), ele nao sai sozinho para outra cena.
	public static bool BlockLocalLoad(Scenes scene)
	{
		if (!running || IsHost || clientLoading)
		{
			return false;
		}
		NetLog.Info("[Netcode] amigo: ignorando troca de cena local para " + scene + " (quem decide e o host)");
		return true;
	}

	// SceneLoader: espera o outro PC chegar no mesmo ponto do carregamento e segue junto
	public static IEnumerator SceneBarrier(int stage, string scene)
	{
		if (!running)
		{
			yield break;
		}
		string key = stage + ":" + scene;
		Status = "Esperando o outro jogador carregar...";
		float start = Time.realtimeSinceStartup;
		int target;
		int seed;
		if (IsHost)
		{
			while (running && Time.realtimeSinceStartup - start < BarrierTimeout)
			{
				lock (sync)
				{
					if (peerReady.Remove(key))
					{
						break;
					}
				}
				ProcessQueue();
				yield return null;
			}
			if (Time.realtimeSinceStartup - start >= BarrierTimeout)
			{
				NetLog.Info("[Netcode] barreira sem resposta do amigo: " + key);
			}
			target = frame + inputDelay + 2;
			seed = new System.Random().Next();
			byte stageByte = (byte)stage;
			int t = target;
			int s = seed;
			// Antes de ativar a cena o amigo recebe o save do host: o que divergiu (moedas, fases) volta igual
			string save = ((stage == 0) ? PlayerData.NetExportCurrent() : string.Empty);
			Send(NetProtocol.SceneGo, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(stageByte);
				w.Write(scene);
				w.Write(t);
				w.Write(s);
				w.Write(save);
			}));
		}
		else
		{
			byte stageByte2 = (byte)stage;
			Send(NetProtocol.SceneReady, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(stageByte2);
				w.Write(scene);
			}));
			while (running && (!pendingGo.HasValue || pendingGo.Value.key != key) && Time.realtimeSinceStartup - start < BarrierTimeout)
			{
				ProcessQueue();
				yield return null;
			}
			if (!pendingGo.HasValue || pendingGo.Value.key != key)
			{
				NetLog.Info("[Netcode] barreira sem resposta do host: " + key);
				Status = string.Empty;
				yield break;
			}
			target = pendingGo.Value.frame;
			seed = pendingGo.Value.seed;
			pendingGo = null;
		}
		while (running && frame < target)
		{
			yield return null;
		}
		UnityEngine.Random.InitState(seed);
		NetRandom.SeedEffects(seed);
		traceDumpedThisScene = false;
		Status = string.Empty;
		NetLog.Info("[Netcode] barreira " + key + " liberada no frame " + frame + " (" + (Time.realtimeSinceStartup - start).ToString("0.0") + " s)");
	}

	// Chamado por toda leitura de controle (NetInput) e pelo Update: um passo por frame do Unity
	public static void Advance()
	{
		if (!running || advancing || Time.frameCount == lastAdvanceUnityFrame)
		{
			return;
		}
		advancing = true;
		try
		{
			lastAdvanceUnityFrame = Time.frameCount;
			ProcessQueue();
			if (!running)
			{
				return;
			}
			int next = frame + 1;
			int target = next + inputDelay;
			NetProtocol.InputFrame local = SampleLocal();
			localBuf[target % BufferSize] = local;
			byte e = epoch;
			Send(NetProtocol.Input, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(e);
				w.Write(target);
				w.Write((byte)1);
				local.Write(w);
			}));
			Stopwatch waitWatch = Stopwatch.StartNew();
			bool gotRemote = WaitRemote(next);
			double waited = waitWatch.Elapsed.TotalMilliseconds;
			if (waited > 3.0)
			{
				statStalls++;
				statStallMs += waited;
				statStallMax = Math.Max(statStallMax, waited);
			}
			if (!gotRemote)
			{
				StopLockstep("o outro jogador parou de responder");
				return;
			}
			frame = next;
			NetProtocol.InputFrame mine = localBuf[next % BufferSize];
			NetProtocol.InputFrame theirs;
			lock (sync)
			{
				theirs = remoteBuf[next % BufferSize];
			}
			if (IsHost)
			{
				NetInput.SetLockstepFrame(mine, theirs);
			}
			else
			{
				NetInput.SetLockstepFrame(theirs, mine);
			}
			if (frame % SnapshotInterval == 0)
			{
				TakeSnapshot();
			}
			Trace(mine, theirs);
			TestWin();
		}
		finally
		{
			advancing = false;
		}
	}

	// ---------- MonoBehaviour ----------

	private void Awake()
	{
		Instance = this;
		NetLog.Init();
		for (int i = 0; i < BufferSize; i++)
		{
			remoteTag[i] = -1;
		}
	}

	private void Start()
	{
		string[] commandLineArgs = Environment.GetCommandLineArgs();
		for (int i = 0; i < commandLineArgs.Length; i++)
		{
			if (commandLineArgs[i] == "-netdelay" && i + 1 < commandLineArgs.Length && int.TryParse(commandLineArgs[i + 1], out var result))
			{
				inputDelay = Mathf.Clamp(result, 1, 30);
				delayFromArgs = true;
			}
			else if (commandLineArgs[i] == "-netcode-autoload" && i + 1 < commandLineArgs.Length && int.TryParse(commandLineArgs[i + 1], out var result2))
			{
				autoSlot = result2;
			}
			else if (commandLineArgs[i] == "-netcode-testlevel" && i + 1 < commandLineArgs.Length && EnumUtils.TryParse<Levels>(commandLineArgs[i + 1], out var result3))
			{
				testLevel = result3;
			}
			else if (commandLineArgs[i] == "-netcode-testwin" && i + 1 < commandLineArgs.Length && int.TryParse(commandLineArgs[i + 1], out var result4))
			{
				testWinAfter = result4;
			}
			else if (commandLineArgs[i] == "-netcode-testdesync" && i + 1 < commandLineArgs.Length && int.TryParse(commandLineArgs[i + 1], out var result5))
			{
				testDesyncAfter = result5;
			}
			else if (commandLineArgs[i] == "-netcode-trace")
			{
				trace = true;
				NetRandom.Trace = true;
			}
			else if (commandLineArgs[i] == "-netcode-botinput")
			{
				bot = new System.Random((i + 1) * 7919 + Environment.TickCount);
			}
		}
		NetcodeTestTransport.StartFromCommandLine();
	}

	private void Update()
	{
		bool flag;
		bool flag2;
		lock (sync)
		{
			flag = pendingEnd;
			flag2 = pendingBegin;
			pendingEnd = false;
			pendingBegin = false;
		}
		if (flag)
		{
			StopLockstep(pendingEndReason);
			if (!flag2)
			{
				Active = false;
				transport = null;
				return;
			}
		}
		if (autoSlot >= 0 && !autoEntered)
		{
			AutoLoad();
		}
		if (flag2)
		{
			running = false;
			helloSent = false;
			peerHello = false;
			DesyncCount = 0;
			Status = "Conectado. Entrem os dois em um save (mapa) para comecar.";
		}
		if (!Active)
		{
			return;
		}
		if (transport != null && !transport.Connected)
		{
			End("conexao caiu");
			return;
		}
		if (!helloSent && LocalReady())
		{
			helloSent = true;
			Send(NetProtocol.Hello, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(NetProtocol.Version);
				w.Write(NetProtocol.BuildHash);
			}));
		}
		if (!running)
		{
			ProcessQueue();
			if (IsHost && helloSent && peerHello && LocalReady())
			{
				HostStart();
			}
		}
		else
		{
			Advance();
			// -netcode-testlevel X: o host leva os dois para a fase X (testes)
			if (IsHost && testLevel.HasValue && frame > 120 && !SceneLoader.CurrentlyLoading && SceneManager.GetActiveScene().name.StartsWith("scene_map_"))
			{
				Levels value = testLevel.Value;
				testLevel = null;
				SceneLoader.LoadLevel(value, SceneLoader.Transition.Fade);
			}
			if (deferredScene != null && !SceneLoader.CurrentlyLoading)
			{
				string[] array = deferredScene;
				deferredScene = null;
				ClientLoadScene(array);
			}
		}
		if (Time.unscaledTime >= nextStats)
		{
			nextStats = Time.unscaledTime + 10f;
			LogNetStats();
		}
		if (Time.unscaledTime >= nextPing)
		{
			nextPing = Time.unscaledTime + 2f;
			Send(NetProtocol.Ping, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(DateTime.UtcNow.Ticks);
			}));
		}
	}

	private void OnApplicationQuit()
	{
		if (Active)
		{
			Send(NetProtocol.Bye, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write("saiu do jogo");
			}));
		}
		StopLockstep("saiu do jogo");
	}

	private void OnGUI()
	{
		if (!Active)
		{
			return;
		}
		GUI.depth = -1001;
		string text = (running ? ("NETCODE " + (IsHost ? "host" : "amigo") + "  |  frame " + frame + "  |  atraso " + inputDelay + "  |  ping " + ((pingMs >= 0) ? (pingMs + "ms") : "?") + ((DesyncCount > 0) ? ("  |  correcoes " + DesyncCount) : string.Empty)) : "NETCODE: esperando os dois entrarem em um save");
		if (!string.IsNullOrEmpty(Status))
		{
			text = text + "  |  " + Status;
		}
		GUI.color = new Color(0f, 0f, 0f, 0.6f);
		GUI.DrawTexture(new Rect(0f, Screen.height - 52, Screen.width, 22f), Texture2D.whiteTexture);
		GUI.color = Color.white;
		GUI.Label(new Rect(10f, Screen.height - 50, Screen.width, 22f), text);
	}

	private static void LogNetStats()
	{
		string text;
		lock (sync)
		{
			if (statPingCount == 0 && statStalls == 0)
			{
				return;
			}
			text = "[Rede] ping " + ((statPingCount > 0) ? (statPingMin + "/" + statPingSum / statPingCount + "/" + statPingMax + " ms (min/media/max)") : "?") + " | esperando o outro PC: " + statStalls + " vezes, " + statStallMs.ToString("0") + " ms no total, maior " + statStallMax.ToString("0") + " ms | atraso " + inputDelay;
			statPingMin = long.MaxValue;
			statPingMax = 0L;
			statPingSum = 0L;
			statPingCount = 0;
			statStalls = 0;
			statStallMs = 0.0;
			statStallMax = 0.0;
		}
		NetLog.Info(text);
	}

	// ---------- Inicio / fim ----------

	// "Este PC esta dentro de um save": no mapa ou numa fase, sem carregar
	private static bool LocalReady()
	{
		if (!PlayerData.Initialized || SceneLoader.CurrentlyLoading)
		{
			return false;
		}
		string name = SceneManager.GetActiveScene().name;
		return name.StartsWith("scene_map_") || name.StartsWith("scene_level_");
	}

	// -netcode-autoload N: pula a tela de titulo e entra no save N
	private static void AutoLoad()
	{
		if (SceneLoader.CurrentlyLoading)
		{
			return;
		}
		string name = SceneManager.GetActiveScene().name;
		if (name == "scene_title" && !autoTitleDone)
		{
			autoTitleDone = true;
			PlayerManager.NetResetSlots();
			SceneLoader.LoadScene(Scenes.scene_slot_select, SceneLoader.Transition.Fade, SceneLoader.Transition.Fade);
		}
		else if (name == "scene_slot_select" && SlotSelectScreen.NetInstance != null)
		{
			PlayerManager.NetResetSlots();
			autoEntered = SlotSelectScreen.NetInstance.NetAutoEnter(autoSlot);
		}
		else if (name.StartsWith("scene_map_") || name.StartsWith("scene_level_"))
		{
			autoEntered = true;
		}
	}

	private static void HostStart()
	{
		// Atraso automatico pelo ping: metade do RTT em frames + 1 de folga. Usa o pior ping dos
		// ultimos ~30 s, nao uma amostra so: com ping oscilando (VPN, Wi-Fi) uma amostra boa deixava o
		// atraso curto demais e o jogo travava esperando o outro PC sempre que o ping subia.
		long worstPing = pingMs;
		lock (sync)
		{
			foreach (long p in recentPings)
			{
				worstPing = Math.Max(worstPing, p);
			}
		}
		if (!delayFromArgs && worstPing >= 0)
		{
			inputDelay = Mathf.Clamp(Mathf.CeilToInt((float)worstPing / 2f / 16.67f) + 1, 2, 12);
			NetLog.Info("[Rede] atraso " + inputDelay + " frames (pior ping recente " + worstPing + " ms)");
		}
		string save = PlayerData.NetExportCurrent();
		int level = (int)SceneLoader.CurrentLevel;
		int delay = inputDelay;
		byte e;
		lock (sync)
		{
			epoch++;
			ClearRemote();
			e = epoch;
		}
		Send(NetProtocol.Config, NetProtocol.Build(delegate(BinaryWriter w)
		{
			w.Write(e);
			w.Write((byte)delay);
			w.Write(save);
			w.Write(level);
		}));
		StartLockstep();
		// Os dois recomecam do mapa do save do host, carregado ao mesmo tempo
		SceneLoader.LoadScene(PlayerData.Data.CurrentMap, SceneLoader.Transition.Fade, SceneLoader.Transition.Fade);
	}

	// chamar com o lock
	private static void ClearRemote()
	{
		for (int i = 0; i < BufferSize; i++)
		{
			remoteTag[i] = -1;
		}
	}

	private static void StartLockstep()
	{
		lock (sync)
		{
			frame = 0;
			for (int i = 0; i < BufferSize; i++)
			{
				localBuf[i] = default(NetProtocol.InputFrame);
			}
			// os frames 1..atraso ninguem manda: comecam vazios dos dois lados
			for (int j = 1; j <= inputDelay; j++)
			{
				remoteBuf[j] = default(NetProtocol.InputFrame);
				remoteTag[j] = j;
			}
			peerReady.Clear();
			localSnaps.Clear();
			hostSnaps.Clear();
		}
		pendingGo = null;
		deferredScene = null;
		lastAdvanceUnityFrame = Time.frameCount;
		PlayerManager.NetResetSlots();
		NetInput.SetLockstepActive(active: true);
		Time.captureFramerate = TargetFps;
		// Com captureFramerate cada frame do Unity vale 1/60 s de jogo: com vsync num monitor
		// de 120/144 Hz o jogo rodava 2x mais rapido. Trava em 60 quadros por segundo de verdade.
		if (savedVSync < 0)
		{
			savedVSync = QualitySettings.vSyncCount;
			savedTargetFps = Application.targetFrameRate;
			savedFixedDelta = Time.fixedDeltaTime;
		}
		// Fisica com um passo por frame. Com 0.02 s o numero de FixedUpdate em cada frame dependia
		// de ha quanto tempo cada jogo estava aberto, e a fisica (colisoes, gatilhos) divergia.
		Time.fixedDeltaTime = 1f / (float)TargetFps;
		QualitySettings.vSyncCount = 0;
		Application.targetFrameRate = TargetFps;
		Application.runInBackground = true;
		running = true;
		Status = string.Empty;
		NetLog.Info("[Netcode] lockstep iniciado (" + (IsHost ? "host" : "amigo") + ", atraso " + inputDelay + ")");
	}

	private static void StopLockstep(string reason)
	{
		bool flag = running;
		running = false;
		lock (sync)
		{
			Monitor.PulseAll(sync);
		}
		NetInput.SetLockstepActive(active: false);
		Time.captureFramerate = 0;
		if (savedVSync >= 0)
		{
			QualitySettings.vSyncCount = savedVSync;
			Application.targetFrameRate = savedTargetFps;
			Time.fixedDeltaTime = savedFixedDelta;
			savedVSync = -1;
		}
		pendingGo = null;
		deferredScene = null;
		Status = "Online encerrado: " + reason;
		if (flag)
		{
			NetLog.Info("[Netcode] lockstep encerrado: " + reason);
			if (!IsHost)
			{
				// O amigo volta para o proprio save
				PlayerData.NetRestoreBackup();
				SceneLoader.LoadScene(PlayerData.Data.CurrentMap, SceneLoader.Transition.Fade, SceneLoader.Transition.Fade);
			}
		}
	}

	// ---------- Mensagens ----------

	private static void Send(byte type, byte[] payload)
	{
		INetcodeTransport netcodeTransport = transport;
		if (netcodeTransport == null || !netcodeTransport.Connected)
		{
			return;
		}
		try
		{
			netcodeTransport.Send(type, payload);
		}
		catch (Exception ex)
		{
			NetLog.Info("[Netcode] falha ao enviar: " + ex.Message);
		}
	}

	private static void ProcessQueue()
	{
		while (true)
		{
			KeyValuePair<byte, byte[]> keyValuePair;
			lock (sync)
			{
				if (mainQueue.Count == 0)
				{
					break;
				}
				keyValuePair = mainQueue.Dequeue();
			}
			try
			{
				Handle(keyValuePair.Key, NetProtocol.Reader(keyValuePair.Value));
			}
			catch (Exception ex)
			{
				NetLog.Info("[Netcode] erro tratando pacote " + keyValuePair.Key + ": " + ex);
			}
		}
	}

	private static void Handle(byte type, BinaryReader r)
	{
		switch (type)
		{
		case NetProtocol.Hello:
		{
			ushort num = r.ReadUInt16();
			int num2 = r.ReadInt32();
			if (num != NetProtocol.Version)
			{
				Status = "Versao do online diferente (" + num + " x " + NetProtocol.Version + ")";
				End("versao diferente");
				return;
			}
			if (num2 != NetProtocol.BuildHash)
			{
				NetLog.Info("[Netcode] aviso: DLL diferente da do outro PC, pode dessincronizar");
				Status = "Aviso: os dois PCs devem usar a mesma Assembly-CSharp.dll";
			}
			peerHello = true;
			break;
		}
		case NetProtocol.Config:
			if (!IsHost)
			{
				r.ReadByte();
				inputDelay = Mathf.Clamp(r.ReadByte(), 1, 30);
				string json = r.ReadString();
				SceneLoader.SetCurrentLevel((Levels)r.ReadInt32());
				PlayerData.NetImportCurrent(json);
				StartLockstep();
			}
			break;
		case NetProtocol.SceneLoad:
			if (!IsHost && running)
			{
				string[] array = new string[5];
				array[0] = r.ReadString();
				array[1] = r.ReadInt32().ToString();
				array[2] = r.ReadByte().ToString();
				array[3] = r.ReadByte().ToString();
				array[4] = r.ReadByte().ToString();
				if (SceneLoader.CurrentlyLoading)
				{
					if (SceneLoader.SceneName != array[0])
					{
						deferredScene = array;
					}
				}
				else
				{
					ClientLoadScene(array);
				}
			}
			break;
		case NetProtocol.SceneReady:
			if (IsHost)
			{
				int num3 = r.ReadByte();
				string text = r.ReadString();
				lock (sync)
				{
					peerReady.Add(num3 + ":" + text);
				}
			}
			break;
		case NetProtocol.SceneGo:
			if (!IsHost)
			{
				int num4 = r.ReadByte();
				string text2 = r.ReadString();
				PendingGo value = default(PendingGo);
				value.key = num4 + ":" + text2;
				value.frame = r.ReadInt32();
				value.seed = r.ReadInt32();
				string save = r.ReadString();
				if (save.Length > 0)
				{
					PlayerData.NetImportCurrent(save);
				}
				pendingGo = value;
			}
			break;
		case NetProtocol.Snapshot:
			if (!IsHost && running)
			{
				int key = r.ReadInt32();
				Snap value2 = default(Snap);
				value2.hash = r.ReadUInt32();
				int num5 = r.ReadByte();
				value2.players = new NetProtocol.PlayerState[2];
				for (int i = 0; i < num5; i++)
				{
					NetProtocol.PlayerState playerState = NetProtocol.PlayerState.Read(r);
					if (playerState.id < 2)
					{
						value2.players[playerState.id] = playerState;
					}
				}
				value2.bossDamage = r.ReadSingle();
				hostSnaps[key] = value2;
				CompareSnapshots();
			}
			break;
		case NetProtocol.Bye:
			End("o outro jogador saiu (" + r.ReadString() + ")");
			break;
		case NetProtocol.TraceDump:
			if (IsHost && running)
			{
				DumpTrace("amigo viu dessincronia no frame " + r.ReadInt32());
			}
			break;
		case NetProtocol.Resync:
			if (IsHost && running)
			{
				HostResync(r.ReadString());
			}
			break;
		}
	}

	// Host: recarrega a cena atual para os dois (fase recomeca, mapa volta igual)
	private static void HostResync(string reason)
	{
		NetLog.Info("[Netcode] amigo pediu ressincronizar: " + reason);
		if (SceneLoader.CurrentlyLoading || Time.realtimeSinceStartup - lastResync < ResyncInterval)
		{
			return;
		}
		lastResync = Time.realtimeSinceStartup;
		string name = SceneManager.GetActiveScene().name;
		NetLog.Info("[Netcode] ressincronizando: recarregando " + name);
		Status = "Os jogos se separaram, recarregando para os dois...";
		if (Level.Current != null && name.StartsWith("scene_level_"))
		{
			SceneLoader.LoadLevel(Level.Current.CurrentLevel, SceneLoader.Transition.Fade);
		}
		else if (name.StartsWith("scene_map_"))
		{
			SceneLoader.LoadScene(PlayerData.Data.CurrentMap, SceneLoader.Transition.Fade, SceneLoader.Transition.Fade);
		}
	}

	private static void RequestResync(string reason)
	{
		if (Time.realtimeSinceStartup - lastResync < ResyncInterval)
		{
			return;
		}
		lastResync = Time.realtimeSinceStartup;
		NetLog.Info("[Netcode] pedindo ao host para ressincronizar: " + reason);
		Send(NetProtocol.Resync, NetProtocol.Build(delegate(BinaryWriter w)
		{
			w.Write(reason);
		}));
	}

	private static void ClientLoadScene(string[] msg)
	{
		if (!EnumUtils.TryParse<Scenes>(msg[0], out var result))
		{
			NetLog.Info("[Netcode] cena desconhecida: " + msg[0]);
			return;
		}
		SceneLoader.SetCurrentLevel((Levels)int.Parse(msg[1]));
		clientLoading = true;
		try
		{
			SceneLoader.LoadScene(result, (SceneLoader.Transition)byte.Parse(msg[2]), (SceneLoader.Transition)byte.Parse(msg[3]), (SceneLoader.Icon)byte.Parse(msg[4]));
		}
		finally
		{
			clientLoading = false;
		}
	}

	// ---------- Input ----------

	private static NetProtocol.InputFrame SampleLocal()
	{
		// -netcode-botinput: controles aleatorios para testes sem ninguem no teclado
		if (bot != null)
		{
			if (frame >= botNextChange)
			{
				botNextChange = frame + 10 + bot.Next(30);
				uint num = 0u;
				foreach (int num2 in BotButtons)
				{
					if (bot.Next(3) == 0)
					{
						num |= (uint)(1 << num2);
					}
				}
				axesTmp[0] = bot.Next(3) - 1;
				axesTmp[1] = bot.Next(3) - 1;
				axesTmp[2] = 0f;
				axesTmp[3] = 0f;
				botFrame = NetProtocol.InputFrame.From(num, axesTmp);
			}
			return botFrame;
		}
		// Dois jogos no mesmo PC (teste): so a janela com foco joga
		if (!Application.isFocused)
		{
			return default(NetProtocol.InputFrame);
		}
		Player player = ReInput.players.GetPlayer(0);
		if (player == null)
		{
			return default(NetProtocol.InputFrame);
		}
		PinJoysticks(player);
		NetInput.ReadLocal(player, out var buttons, axesTmp);
		return NetProtocol.InputFrame.From(buttons, axesTmp);
	}

	// -netcode-trace: uma linha por frame com o estado que costuma divergir primeiro, para comparar
	// os logs dos dois PCs e achar o frame exato onde os jogos se separam
	private static bool trace;

	private static void Trace(NetProtocol.InputFrame mine, NetProtocol.InputFrame theirs)
	{
		try
		{
			string text = "[Trace] " + frame + " t " + Time.time.ToString("0.0000") + " fixo " + Time.fixedTime.ToString("0.0000") + " rnd " + JsonUtility.ToJson(UnityEngine.Random.state).GetHashCode().ToString("x8") + " in " + mine.buttons.ToString("x") + "/" + theirs.buttons.ToString("x");
			for (int i = 0; i < 2; i++)
			{
				AbstractPlayerController player = PlayerManager.GetPlayer((PlayerId)i);
				if (player != null)
				{
					Vector3 position = player.transform.position;
					text = text + " p" + (i + 1) + " " + position.x.ToString("0.000") + "," + position.y.ToString("0.000") + " v" + ((player.stats != null) ? player.stats.Health : (-1));
				}
			}
			TraceLine(text);
		}
		catch (Exception ex)
		{
			NetLog.Info("[Trace] erro: " + ex.Message);
		}
	}

	// Rastro dos ultimos ~20 s (estado por frame e cada sorteio) guardado na memoria. Na primeira
	// dessincronia de cada cena os dois PCs gravam o rastro no log: comparando os dois da para ver o
	// frame e o sorteio exato onde os jogos se separaram, mesmo sem -netcode-trace.
	private static readonly string[] traceRing = new string[8000];

	private static int traceHead;

	private static int traceCount;

	private static bool traceDumpedThisScene;

	public static void TraceLine(string line)
	{
		if (trace)
		{
			NetLog.Info(line);
		}
		lock (traceRing)
		{
			traceRing[traceHead] = line;
			traceHead = (traceHead + 1) % traceRing.Length;
			traceCount = Math.Min(traceCount + 1, traceRing.Length);
		}
	}

	private static void DumpTrace(string reason)
	{
		System.Text.StringBuilder sb = new System.Text.StringBuilder();
		lock (traceRing)
		{
			int start = (traceHead - traceCount + traceRing.Length) % traceRing.Length;
			for (int i = 0; i < traceCount; i++)
			{
				sb.Append("    ").Append(traceRing[(start + i) % traceRing.Length]).Append(Environment.NewLine);
			}
		}
		NetLog.Info("[Rastro] inicio (" + reason + "), frame " + frame + Environment.NewLine + sb.ToString() + "[Rastro] fim");
	}

	private static void TestWin()
	{
		if ((testWinAfter < 0 && testDesyncAfter < 0) || Level.Current == null)
		{
			return;
		}
		if (Level.Current != testWinLevel)
		{
			testWinLevel = Level.Current;
			testWinStart = frame;
		}
		else if (!IsHost && frame - testWinStart == testDesyncAfter && PlayerManager.GetPlayer(PlayerId.PlayerOne) != null)
		{
			// -netcode-testdesync N: so no amigo o player 1 fica sem vida (simula os jogos se separando)
			testDesyncAfter = -1;
			NetLog.Info("[Netcode] teste: zerando a vida do player 1 so no amigo no frame " + frame);
			PlayerManager.GetPlayer(PlayerId.PlayerOne).stats.SetHealth(0);
		}
		else if (frame - testWinStart == testWinAfter && !Level.Current.Ending)
		{
			NetLog.Info("[Netcode] teste: chefe derrotado no frame " + frame);
			typeof(Level).GetMethod("zHack_OnWin", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(Level.Current, null);
		}
	}

	// Cada PC tem um jogador so: todo controle ligado nele joga pelo player local (Rewired 0).
	// Sem isso o jogo dava o controle ao player 2 quando o amigo apertava um botao para entrar
	// (ou quando o controle era ligado depois), e o controle parava de responder no online.
	private static void PinJoysticks(Player player)
	{
		foreach (Joystick joystick in ReInput.controllers.Joysticks)
		{
			if (!player.controllers.ContainsController(joystick))
			{
				player.controllers.AddController(joystick, removeFromOtherPlayers: true);
				ReInput.userDataStore.LoadControllerData(player.id, ControllerType.Joystick, joystick.id);
				NetLog.Info("[Netcode] controle no player local: " + joystick.name);
			}
		}
	}

	private static bool WaitRemote(int f)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		lock (sync)
		{
			while (remoteTag[f % BufferSize] != f)
			{
				if (pendingEnd || transport == null || !transport.Connected || stopwatch.Elapsed.TotalSeconds > (double)StallTimeout)
				{
					return false;
				}
				Monitor.Wait(sync, 20);
			}
		}
		return true;
	}

	// ---------- Checagem de dessincronia ----------

	private static Snap Capture()
	{
		Snap result = default(Snap);
		result.players = new NetProtocol.PlayerState[2];
		uint num = 2166136261u;
		for (int i = 0; i < 2; i++)
		{
			NetProtocol.PlayerState playerState = default(NetProtocol.PlayerState);
			playerState.id = (byte)i;
			try
			{
				AbstractPlayerController player = PlayerManager.GetPlayer((PlayerId)i);
				if (player != null)
				{
					playerState.present = true;
					Vector3 position = player.transform.position;
					playerState.x = position.x;
					playerState.y = position.y;
					if (player.stats != null)
					{
						playerState.hp = player.stats.Health;
						playerState.super = player.stats.SuperMeter;
					}
				}
				else
				{
					Transform mapPlayer = MapPlayer(i);
					if (mapPlayer != null)
					{
						playerState.present = true;
						playerState.x = mapPlayer.position.x;
						playerState.y = mapPlayer.position.y;
					}
				}
			}
			catch (Exception)
			{
			}
			result.players[i] = playerState;
			num = Mix(num, playerState.present ? 1 : 0);
			num = Mix(num, Mathf.RoundToInt(playerState.x));
			num = Mix(num, Mathf.RoundToInt(playerState.y));
			num = Mix(num, playerState.hp);
		}
		result.bossDamage = -1f;
		try
		{
			if (Level.Current != null && Level.Current.timeline != null)
			{
				result.bossDamage = Level.Current.timeline.damage;
			}
		}
		catch (Exception)
		{
		}
		num = Mix(num, Mathf.RoundToInt(result.bossDamage));
		result.hash = num;
		return result;
	}

	// No mapa os jogadores sao MapPlayerController, fora do PlayerManager
	private static Transform MapPlayer(int i)
	{
		Map current = Map.Current;
		if (current == null || current.players == null || i >= current.players.Length || current.players[i] == null)
		{
			return null;
		}
		return current.players[i].transform;
	}

	private static uint Mix(uint h, int v)
	{
		for (int i = 0; i < 4; i++)
		{
			h ^= (uint)((v >> i * 8) & 0xFF);
			h *= 16777619;
		}
		return h;
	}

	private static void TakeSnapshot()
	{
		Snap snap = Capture();
		int f = frame;
		// Linha no log para comparar os dois PCs nos testes
		if (frame % (SnapshotInterval * 10) == 0)
		{
			NetLog.Info("[Netcode] snap frame " + f + " hash " + snap.hash.ToString("x8") + " cena " + SceneManager.GetActiveScene().name + " p1 " + snap.players[0].x.ToString("0.0") + "," + snap.players[0].y.ToString("0.0") + " p2 " + (snap.players[1].present ? (snap.players[1].x.ToString("0.0") + "," + snap.players[1].y.ToString("0.0")) : "-") + " chefe " + snap.bossDamage.ToString("0.0"));
		}
		if (IsHost)
		{
			Send(NetProtocol.Snapshot, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(f);
				w.Write(snap.hash);
				w.Write((byte)2);
				snap.players[0].Write(w);
				snap.players[1].Write(w);
				w.Write(snap.bossDamage);
			}));
		}
		else
		{
			localSnaps[f] = snap;
			CompareSnapshots();
		}
	}

	// Amigo: compara o estado do host com o proprio no mesmo frame. Se divergiu, puxa os
	// jogadores para onde o host diz (aplica a diferenca, ja que o amigo pode estar alguns
	// frames a frente) e copia a vida.
	private static void CompareSnapshots()
	{
		List<int> list = null;
		foreach (KeyValuePair<int, Snap> hostSnap in hostSnaps)
		{
			if (!localSnaps.TryGetValue(hostSnap.Key, out var value))
			{
				continue;
			}
			if (list == null)
			{
				list = new List<int>();
			}
			list.Add(hostSnap.Key);
			if (value.hash != hostSnap.Value.hash)
			{
				DesyncCount++;
				if (!traceDumpedThisScene)
				{
					traceDumpedThisScene = true;
					DumpTrace("dessincronia no frame " + hostSnap.Key);
					Send(NetProtocol.TraceDump, NetProtocol.Build(delegate(BinaryWriter w)
					{
						w.Write(hostSnap.Key);
					}));
				}
				NetLog.Info("[Netcode] dessincronia no frame " + hostSnap.Key + " cena " + SceneManager.GetActiveScene().name + ", corrigindo pelos dados do host" + Environment.NewLine + "    host:  " + Describe(hostSnap.Value) + Environment.NewLine + "    amigo: " + Describe(value));
				string severe = Correct(hostSnap.Value, value);
				// So recarrega se continuar separado em duas checagens seguidas e fora de um carregamento
				severeStreak = ((severe != null && !SceneLoader.CurrentlyLoading) ? (severeStreak + 1) : 0);
				if (severeStreak >= 2)
				{
					severeStreak = 0;
					RequestResync(severe);
				}
			}
			else
			{
				severeStreak = 0;
			}
		}
		if (list != null)
		{
			foreach (int item in list)
			{
				hostSnaps.Remove(item);
				localSnaps.Remove(item);
			}
		}
		// descarta o que ficou velho (cena trocada, etc.)
		if (localSnaps.Count > 20)
		{
			localSnaps.Clear();
		}
		if (hostSnaps.Count > 20)
		{
			hostSnaps.Clear();
		}
	}

	private static string Describe(Snap s)
	{
		string text = "hash " + s.hash.ToString("x8");
		for (int i = 0; i < 2; i++)
		{
			NetProtocol.PlayerState p = s.players[i];
			text += "  p" + (i + 1) + (p.present ? (" " + p.x.ToString("0.0") + "," + p.y.ToString("0.0") + " vida " + p.hp) : " ausente");
		}
		return text + "  chefe " + s.bossDamage.ToString("0.0");
	}

	// Corrige o que da (posicao, vida). Devolve o motivo quando nao da para corrigir assim e
	// precisa recarregar a cena para os dois.
	private static string Correct(Snap host, Snap local)
	{
		string severe = null;
		for (int i = 0; i < 2; i++)
		{
			NetProtocol.PlayerState playerState = host.players[i];
			NetProtocol.PlayerState playerState2 = local.players[i];
			if (playerState.present != playerState2.present)
			{
				severe = "player " + (i + 1) + (playerState.present ? " sumiu so no amigo" : " so existe no amigo");
				continue;
			}
			if (!playerState.present)
			{
				continue;
			}
			if (SceneManager.GetActiveScene().name.StartsWith("scene_level_") && (playerState.hp > 0) != (playerState2.hp > 0))
			{
				severe = "player " + (i + 1) + ((playerState.hp > 0) ? " morto so no amigo" : " morto so no host");
			}
			try
			{
				AbstractPlayerController player = PlayerManager.GetPlayer((PlayerId)i);
				Transform transform = ((player != null) ? player.transform : MapPlayer(i));
				if (transform != null)
				{
					Vector3 position = transform.position;
					position.x += playerState.x - playerState2.x;
					position.y += playerState.y - playerState2.y;
					transform.position = position;
				}
				if (!(player == null))
				{
					if (player.stats != null && playerState.hp != playerState2.hp && playerState.hp > 0 && player.stats.Health > 0)
					{
						player.stats.SetHealth(playerState.hp);
					}
				}
			}
			catch (Exception ex)
			{
				NetLog.Info("[Netcode] falha ao corrigir player " + i + ": " + ex.Message);
			}
		}
		return severe;
	}
}
