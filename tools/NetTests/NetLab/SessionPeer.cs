using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetLab;

// Par simulado do netcode em lockstep (NetProtocol v2: Input e Config comecam com o numero da partida).
// Cada lado so avanca o frame F quando tem o input dos dois para F; o input local
// vale para F + inputDelay. A "simulacao" e um hash dos inputs, entao os dois lados
// tem que chegar no mesmo checksum: se nao chegarem, o protocolo perdeu/duplicou input.
public sealed class LockstepBot
{
	public const byte Hello = 0x20, Config = 0x21, Input = 0x22, InputAck = 0x23, Checksum = 0x27, Ping = 0x2A, Pong = 0x2B, Bye = 0x2C;

	public const ushort Version = 2;

	public const byte Match = 1;

	private readonly FrameConn conn;

	private readonly bool isHost;

	private readonly string script;

	private readonly object sync = new();

	private readonly Dictionary<int, uint> local = new();

	private readonly Dictionary<int, uint> remote = new();

	private readonly Dictionary<int, uint> myChecks = new();

	private readonly Dictionary<int, uint> theirChecks = new();

	private int inputDelay = 3;

	private int remoteAckedUpTo = -1;

	private int lastRemoteContiguous = -1;

	private bool configured;

	private volatile string error;

	public int Frame;

	public int StallTicks;

	public int Ticks;

	public int Desyncs;

	public int ChecksMatched;

	public int InputsResent;

	public double PingMs = -1;

	public uint State = 2166136261;

	public LockstepBot(FrameConn conn, bool isHost, string script, int inputDelay = 3)
	{
		this.conn = conn;
		this.isHost = isHost;
		this.script = script;
		this.inputDelay = inputDelay;
		configured = isHost;
	}

	public string Error => error;

	private static byte[] Build(Action<BinaryWriter> f)
	{
		var ms = new MemoryStream();
		var w = new BinaryWriter(ms, Encoding.UTF8);
		f(w);
		w.Flush();
		return ms.ToArray();
	}

	private void ReadLoop()
	{
		try
		{
			while (error == null)
			{
				var (t, p) = conn.Receive();
				var r = new BinaryReader(new MemoryStream(p), Encoding.UTF8);
				switch (t)
				{
				case Hello:
					if (r.ReadUInt16() != Version)
					{
						error = "versao do protocolo diferente";
					}
					break;
				case Config:
					lock (sync)
					{
						r.ReadByte();
						inputDelay = r.ReadByte();
						configured = true;
					}
					break;
				case Input:
				{
					if (r.ReadByte() != Match)
					{
						break;
					}
					int first = r.ReadInt32();
					int n = r.ReadByte();
					lock (sync)
					{
						for (int i = 0; i < n; i++)
						{
							uint b = r.ReadUInt32();
							r.ReadBytes(4);
							int f = first + i;
							if (remote.TryGetValue(f, out uint old) && old != b)
							{
								error = $"input do frame {f} mudou ao ser reenviado";
							}
							remote[f] = b;
						}
						while (remote.ContainsKey(lastRemoteContiguous + 1))
						{
							lastRemoteContiguous++;
						}
					}
					conn.Send(InputAck, BitConverter.GetBytes(lastRemoteContiguous));
					break;
				}
				case InputAck:
					lock (sync)
					{
						remoteAckedUpTo = Math.Max(remoteAckedUpTo, r.ReadInt32());
					}
					break;
				case Checksum:
					lock (sync)
					{
						theirChecks[r.ReadInt32()] = r.ReadUInt32();
					}
					break;
				case Ping:
					conn.Send(Pong, p);
					break;
				case Pong:
					PingMs = (Stopwatch.GetTimestamp() - r.ReadInt64()) * 1000.0 / Stopwatch.Frequency;
					break;
				case Bye:
					error ??= "o outro lado saiu: " + r.ReadString();
					break;
				default:
					// tipos do lobby (0x00-0x1F) e do netcode que o bot nao usa: ignora
					break;
				}
			}
		}
		catch (Exception ex)
		{
			error ??= "conexao: " + ex.Message;
		}
	}

	// Roda em tempo real a 60 ticks/s por 'seconds'
	public void Run(double seconds)
	{
		new Thread(ReadLoop) { IsBackground = true }.Start();
		conn.Send(Hello, Build(w => { w.Write(Version); w.Write(12345); }));
		if (isHost)
		{
			conn.Send(Config, Build(w => { w.Write(Match); w.Write((byte)inputDelay); w.Write("{}"); w.Write(0); }));
		}
		var sw = Stopwatch.StartNew();
		double tickMs = 1000.0 / 60;
		double next = 0;
		int lastSentFrame = -1;
		try
		{
			Loop(sw, seconds, tickMs, ref next, ref lastSentFrame);
		}
		catch (Exception ex)
		{
			error ??= "envio: " + ex.Message;
		}
		try
		{
			conn.Send(Bye, Build(w => w.Write("fim do teste")));
		}
		catch
		{
		}
	}

	private void Loop(Stopwatch sw, double seconds, double tickMs, ref double next, ref int lastSentFrame)
	{
		while (sw.Elapsed.TotalSeconds < seconds && error == null)
		{
			next += tickMs;
			double wait = next - sw.Elapsed.TotalMilliseconds;
			if (wait > 0)
			{
				Thread.Sleep(TimeSpan.FromMilliseconds(wait));
			}
			Ticks++;
			if (Ticks % 30 == 0)
			{
				conn.Send(Ping, BitConverter.GetBytes(Stopwatch.GetTimestamp()));
			}
			lock (sync)
			{
				if (!configured)
				{
					continue;
				}
				// primeiros frames (0..delay-1) sao vazios dos dois lados
				for (int f = 0; f < inputDelay; f++)
				{
					local.TryAdd(f, 0);
					remote.TryAdd(f, 0);
				}
				while (remote.ContainsKey(lastRemoteContiguous + 1))
				{
					lastRemoteContiguous++;
				}
				int target = Frame + inputDelay;
				if (!local.ContainsKey(target))
				{
					var (b, _, _) = Legacy.Script(script, target / 60.0);
					local[target] = b ^ (isHost ? 0u : 0x80000000u);
				}
			}
			SendInputs(ref lastSentFrame);
			lock (sync)
			{
				if (!remote.TryGetValue(Frame, out uint theirs))
				{
					StallTicks++;
					continue;
				}
				uint mine = local[Frame];
				uint p1 = isHost ? mine : theirs, p2 = isHost ? theirs : mine;
				State = (State ^ p1) * 16777619u;
				State = (State ^ p2) * 16777619u;
				if (Frame % 60 == 0)
				{
					myChecks[Frame] = State;
					int fr = Frame;
					uint st = State;
					conn.Send(Checksum, Build(w => { w.Write(fr); w.Write(st); }));
				}
				foreach (int f in myChecks.Keys.ToList())
				{
					if (theirChecks.TryGetValue(f, out uint other))
					{
						if (other == myChecks[f])
						{
							ChecksMatched++;
						}
						else
						{
							Desyncs++;
						}
						myChecks.Remove(f);
						theirChecks.Remove(f);
					}
				}
				Frame++;
			}
		}
	}

	// Reenvia tudo que o outro lado ainda nao confirmou (ate 64 frames por pacote)
	private void SendInputs(ref int lastSentFrame)
	{
		int first, count;
		uint[] buttons;
		lock (sync)
		{
			first = remoteAckedUpTo + 1;
			int last = local.Keys.Max();
			count = Math.Min(64, last - first + 1);
			if (count <= 0)
			{
				return;
			}
			if (first <= lastSentFrame)
			{
				InputsResent += Math.Min(count, lastSentFrame - first + 1);
			}
			lastSentFrame = Math.Max(lastSentFrame, first + count - 1);
			buttons = new uint[count];
			for (int i = 0; i < count; i++)
			{
				buttons[i] = local[first + i];
			}
		}
		int f0 = first;
		conn.Send(Input, Build(w =>
		{
			w.Write(Match);
			w.Write(f0);
			w.Write((byte)buttons.Length);
			foreach (uint b in buttons)
			{
				w.Write(b);
				w.Write(0);
			}
		}));
	}

	public override string ToString() =>
		$"frame {Frame} em {Ticks} ticks ({100.0 * Frame / Math.Max(1, Ticks):0}% da velocidade), {StallTicks} ticks travado esperando input, ping {PingMs:0} ms, " +
		$"{ChecksMatched} checksums iguais, {Desyncs} DESSINCRONIAS" + (error != null ? " | erro: " + error : "");
}

public static class SessionPeer
{
	public static int Run(Args a)
	{
		double seconds = a.Num("seconds", 30);
		string script = a.Str("script", "mash");
		int delay = a.Int("input-delay", 3);
		TcpClient tcp;
		bool host;
		if (a.Has("listen"))
		{
			var l = new TcpListener(IPAddress.Any, a.Int("listen", 7777));
			l.Start();
			Log.Info("peer", "esperando conexao na porta " + a.Int("listen", 7777));
			tcp = l.AcceptTcpClient();
			l.Stop();
			host = true;
		}
		else
		{
			tcp = new TcpClient();
			tcp.Connect(Args.ParseEndPoint(a.Str("connect", "127.0.0.1:7777"), 7777));
			host = false;
		}
		Log.Info("peer", (host ? "host" : "amigo") + " conectado: " + tcp.Client.RemoteEndPoint);
		using var conn = new FrameConn(tcp);
		var bot = new LockstepBot(conn, host, script, delay);
		bot.Run(seconds);
		Log.Info("peer", bot.ToString());
		return bot.Error == null || bot.Error.StartsWith("o outro lado saiu") ? (bot.Desyncs == 0 ? 0 : 1) : 1;
	}

	// Dois bots pelo proxy: mede quanto o lockstep trava com cada latencia
	public static void SelfTest(Action<string, bool, string> check, double seconds)
	{
		Scenario(check, "lockstep rede local", new LinkConditions(), 3, seconds, minSpeed: 95);
		Scenario(check, "lockstep 20ms de ida, delay 3", new LinkConditions { DelayMs = 20, JitterMs = 5 }, 3, seconds, minSpeed: 90);
		Scenario(check, "lockstep 60ms de ida, delay 3", new LinkConditions { DelayMs = 60, JitterMs = 10 }, 3, seconds, minSpeed: 0);
		Scenario(check, "lockstep 60ms de ida, delay 8", new LinkConditions { DelayMs = 60, JitterMs = 10 }, 8, seconds, minSpeed: 90);
		Scenario(check, "lockstep 60ms de ida perda 1%, delay 8", new LinkConditions { DelayMs = 60, JitterMs = 10, LossPct = 1 }, 8, seconds, minSpeed: 0);
		Scenario(check, "lockstep queda no meio", new LinkConditions { DelayMs = 20, CutAfterS = seconds / 2 }, 3, seconds, minSpeed: 0, expectDrop: true);
	}

	private static void Scenario(Action<string, bool, string> check, string name, LinkConditions c, int delay, double seconds, double minSpeed, bool expectDrop = false)
	{
		var l = new TcpListener(IPAddress.Loopback, 0);
		l.Start();
		int port = ((IPEndPoint)l.LocalEndpoint).Port;
		var (pep, cts) = SelfTest_StartProxy(port, c);
		LockstepBot hb = null;
		var ht = Task.Run(() =>
		{
			using var conn = new FrameConn(l.AcceptTcpClient());
			hb = new LockstepBot(conn, true, "mash", delay);
			hb.Run(seconds);
		});
		var tcp = new TcpClient();
		tcp.Connect(pep);
		using (var conn = new FrameConn(tcp))
		{
			var cb = new LockstepBot(conn, false, "walk", delay);
			cb.Run(seconds);
			ht.Wait(5000);
			double speed = 100.0 * Math.Min(hb.Frame, cb.Frame) / Math.Max(1, cb.Ticks);
			bool sameFrame = Math.Abs(hb.Frame - cb.Frame) <= 2 * delay + 64;
			bool ok = expectDrop
				? cb.Error != null && cb.Desyncs == 0 && hb.Desyncs == 0
				: hb.Desyncs == 0 && cb.Desyncs == 0 && cb.ChecksMatched > 0 && speed >= minSpeed && sameFrame
				  && (cb.Error == null || cb.Error.StartsWith("o outro lado saiu"));
			string info = minSpeed == 0 && !expectDrop ? " (so medicao)" : "";
			check(name + info, ok, $"velocidade {speed:0}% | host: {hb} | amigo: {cb}");
		}
		cts.Cancel();
		l.Stop();
	}

	private static (IPEndPoint, CancellationTokenSource) SelfTest_StartProxy(int port, LinkConditions c) => NetLab.SelfTest.StartProxy(port, c);
}
